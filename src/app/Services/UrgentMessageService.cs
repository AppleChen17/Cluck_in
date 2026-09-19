using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using CluckIn.App.Models;

namespace CluckIn.App.Services;

// All state transitions occur on the WPF dispatcher, including API delivery.
public sealed class UrgentMessageService(IHttpClientFactory clients)
{
    private readonly Queue<UrgentMessage> _pending = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly HashSet<string> _processed = new(StringComparer.Ordinal);
    private string? _cursor;
    private bool _polling;
    public UrgentMessage? Current => _pending.TryPeek(out var message) ? message : null;
    public int Count => _pending.Count;
    public string Status { get; private set; } = "Urgent messages: waiting for message service";

    public bool Enqueue(UrgentMessage alert)
    {
        if (alert.Message is null || string.IsNullOrWhiteSpace(alert.Message.Id) ||
            string.IsNullOrWhiteSpace(alert.Message.Sender) || string.IsNullOrWhiteSpace(alert.Message.Content) ||
            alert.Message.Source is not ("gmail" or "slack") || string.IsNullOrWhiteSpace(alert.Reason))
            throw new ArgumentException("A message ID, gmail/slack source, sender, content and reason are required.");
        if (alert.Message.Id.Length > 512 || alert.Message.Sender.Length > 500 ||
            alert.Message.Content.Length > 50000 || alert.Message.Title?.Length > 1000 || alert.Reason.Length > 4000)
            throw new ArgumentException("Urgent message exceeds the display limits.");
        if (_seen.Contains(alert.Message.Id)) return false;
        if (_pending.Count >= 100) throw new InvalidOperationException("Urgent message queue is full; acknowledge a message and retry.");
        _seen.Add(alert.Message.Id);
        _pending.Enqueue(alert);
        return true;
    }

    public void Acknowledge(string id)
    {
        if (Current?.Message.Id == id) _pending.Dequeue();
    }

    public async Task PollAsync(object context, CancellationToken cancellationToken)
    {
        if (_polling) return;
        _polling = true;
        var stage = "message service";
        var endpoint = "";
        try
        {
            using var external = clients.CreateClient("ExternalMessages");
            using var ai = clients.CreateClient("TaskAnalysis");
            endpoint = new Uri(external.BaseAddress!, "health").ToString();
            var health = await external.GetFromJsonAsync<ExternalHealth>("health", cancellationToken)
                ?? throw new JsonException("Empty message service health response.");
            if (health.Adapters is null) throw new JsonException("Missing message adapter status.");
            if (health.Adapters.Any(adapter => adapter.Name == "fixture" && adapter.Enabled))
            {
                // Keep explicitly requested local previews visible until acknowledged.
                var previews = _pending.Where(alert => alert.Message.Id.StartsWith("preview:", StringComparison.Ordinal)).ToArray();
                _pending.Clear();
                foreach (var preview in previews) _pending.Enqueue(preview);
                Status = "目前是測試資料來源（fixture），已停用自動提醒。請在 external 設定真實 Gmail／Slack 來源並移除 fixture。";
                return;
            }
            if (!health.Adapters.Any(adapter => adapter.Enabled && adapter.Connected && adapter.Name is "gmail" or "slack"))
            {
                Status = "尚未連接真實 Gmail／Slack 來源，請檢查 external 帳號設定與連線狀態。";
                return;
            }
            var path = "messages?limit=20" + (_cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(_cursor));
            endpoint = new Uri(external.BaseAddress!, "messages").ToString();
            var batch = await external.GetFromJsonAsync<MessageBatch>(path, cancellationToken)
                ?? throw new JsonException("Empty messages response.");
            if (batch.Messages is null || batch.Cursor is null) throw new JsonException("Invalid messages response.");
            foreach (var message in batch.Messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!message.Unread || _processed.Contains(message.Id) || _seen.Contains(message.Id)) continue;
                stage = "AI service";
                endpoint = new Uri(ai.BaseAddress!, "analyze-message").ToString();
                using var response = await ai.PostAsJsonAsync("analyze-message", new { message, context }, cancellationToken);
                response.EnsureSuccessStatusCode();
                var decision = await response.Content.ReadFromJsonAsync<MessageDecision>(cancellationToken);
                if (decision is null || decision.MessageId != message.Id || decision.Decision is not ("urgent" or "allow" or "hold"))
                    throw new JsonException("Invalid message analysis response.");
                cancellationToken.ThrowIfCancellationRequested();
                if (decision.Decision == "urgent") Enqueue(new(message, decision.Reason));
                _processed.Add(message.Id);
            }
            _cursor = batch.Cursor;
            Status = "Urgent messages: connected";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            // Preserve cursor so failed analysis/delivery is retried; never log message content.
            var detail = exception is HttpRequestException { StatusCode: { } code }
                ? $"HTTP {(int)code}"
                : exception is HttpRequestException ? "connection failed; check that the service is running"
                : exception is OperationCanceledException ? "request timed out"
                : exception.GetType().Name;
            Status = $"Urgent messages unavailable: {stage} {endpoint} — {detail}. Retrying automatically.";
        }
        finally { _polling = false; }
    }

    public sealed record MessageBatch(ExternalMessage[] Messages, string Cursor);
    public sealed record ExternalHealth(AdapterStatus[] Adapters);
    public sealed record AdapterStatus(string Name, bool Enabled, bool Connected);
    public sealed record MessageDecision(string MessageId, string Decision, string Reason);
}
