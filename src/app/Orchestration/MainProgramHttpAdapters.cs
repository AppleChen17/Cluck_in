using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using CluckIn.App.Interfaces;
using CluckIn.App.Models;

namespace CluckIn.App.Orchestration;

// Python services are shared, externally hosted modules. This adapter owns only
// the Focus client session; stopping Focus must not kill task-analysis service.
public sealed class FocusAIClient(HttpClient client, Func<string?> currentTask) : IFocusAIEngine
{
    private string? _session;
    private DateTimeOffset _started;
    private int _mode;
    public void SetAutomationMode(AIAssistRoutingMode mode) => Volatile.Write(ref _mode, (int)mode);

    public async Task StartAsync(string focusSessionId, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("health", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        _started = DateTimeOffset.UtcNow;
        _session = focusSessionId;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _session = null;
        return Task.CompletedTask;
    }

    private object Context(string session)
    {
        if (_session != session) throw new OperationCanceledException("Focus AI session is no longer active.");
        return new {
            mode = "focus", currentTask = currentTask(),
            automationMode = ((AIAssistRoutingMode)Volatile.Read(ref _mode)).ToString().ToLowerInvariant(),
            focusStartedAt = _started, metadata = new { focusSessionId = session }
        };
    }

    public async Task<AIDecisionContract> AnalyzeMessageAsync(
        ExternalMessageContract message, string focusSessionId, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync("analyze-message",
            new { message, context = Context(focusSessionId) }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AIDecisionContract>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("AI returned no decision.");
    }

    // The canonical analyze response already includes the draft. No second HTTP request.
    public Task<string?> DraftReplyAsync(ExternalMessageContract message, AIDecisionContract decision,
        string focusSessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(decision.ReplyDraft);
    }

    public async Task<string> SummarizeAsync(IReadOnlyList<FocusMessageRecord> messages,
        string focusSessionId, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync("summarize-messages",
            new { messages = messages.Select(m => m.Message), context = Context(focusSessionId) },
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        return body.GetProperty("summary").GetString() ?? throw new InvalidOperationException("AI returned no summary.");
    }
}

public sealed class ExternalMessageListener(HttpClient client, Action<string> log) : IMessageListener
{
    private CancellationTokenSource? _cancellation;
    private Task? _poll;
    private string? _cursor;
    public event Func<ExternalMessageContract, Task>? MessageReceived;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_poll is not null) return;
        using var response = await client.GetAsync("health", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        _cancellation = new();
        // Capture subscribers once: callbacks from this run cannot enter a later session.
        var handlers = MessageReceived;
        var token = _cancellation.Token;
        _poll = Task.Run(() => PollAsync(handlers, token), CancellationToken.None);
    }

    private async Task PollAsync(Func<ExternalMessageContract, Task>? handlers, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var url = "messages?limit=100" + (_cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(_cursor));
                var page = await client.GetFromJsonAsync<MessagePage>(url, token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("External service returned no message page.");
                foreach (var message in page.Messages)
                {
                    token.ThrowIfCancellationRequested();
                    if (handlers is null) continue;
                    foreach (Func<ExternalMessageContract, Task> handler in handlers.GetInvocationList())
                    {
                        try { await handler(message).WaitAsync(token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception ex) { log($"Message callback failed: {ex}"); }
                    }
                }
                _cursor = page.Cursor;
                if (page.HasMore) continue;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { log($"External message polling failed: {ex}"); }
            try { await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var cancellation = _cancellation;
        if (cancellation is null) return;
        cancellation.Cancel();
        if (_poll is not null) await _poll.ConfigureAwait(false);
        _poll = null;
        _cancellation = null;
        cancellation.Dispose();
    }

    private sealed record MessagePage(ExternalMessageContract[] Messages, string Cursor, bool HasMore);
}

public sealed class ExternalReplyClient(HttpClient client, Action<string> log) : IExternalReplyService
{
    public async Task<ExternalReplyResult> SendReplyAsync(ExternalMessageContract message, string reply,
        string idempotencyKey, CancellationToken cancellationToken)
    {
        // /reply deduplicates by messageId in the existing external outbox.
        // Its strict contract does not accept an extra idempotencyKey field.
        using var response = await client.PostAsJsonAsync("reply",
            new { messageId = message.Id, body = reply }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        var error = result.TryGetProperty("error", out var value) ? value.GetString() : null;
        var delivered = result.GetProperty("delivered").GetBoolean();
        var dryRun = result.GetProperty("dryRun").GetBoolean();
        log($"External reply result: delivered={delivered}, dryRun={dryRun}, error={error}");
        return new() { Success = error is null && (delivered || dryRun),
            Error = error ?? (delivered || dryRun ? null : "External reply was not delivered.") };
    }
}
