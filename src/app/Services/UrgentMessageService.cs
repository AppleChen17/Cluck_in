using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using CluckIn.App.Models;
using Microsoft.Extensions.Logging;

namespace CluckIn.App.Services;

// All state transitions occur on the WPF dispatcher, including API delivery.
public sealed class UrgentMessageService(IHttpClientFactory clients, ILogger<UrgentMessageService>? logger = null)
{
    // "Have I already shown this?" is the consumer's job, and it has to outlive
    // the process. The external buffer is in memory: after ITS restart GET
    // /messages replays everything it still holds (replayed: true), and after
    // OURS an in-memory set would re-analyse and re-pop every message the user
    // already dealt with. Message ids are stable across restarts by contract;
    // the cursor is not, which is why the ids are what the guarantee rests on.
    private readonly string _statePath = Environment.GetEnvironmentVariable("CLUCKIN_URGENT_STATE_PATH") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CluckIn", "urgent-messages.json");
    // Ids run about 50 bytes, so this is a few hundred KB and months of traffic.
    private const int HandledLimit = 5000;

    private readonly Queue<UrgentMessage> _pending = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly HashSet<string> _processed = new(StringComparer.Ordinal);
    // Insertion order for _processed, so eviction drops the oldest ids first.
    private readonly List<string> _handled = [];
    private string? _cursor;
    private bool _polling;
    private bool _loaded;
    private bool _dirty;
    // Both load on first read, so an alert left unacknowledged at shutdown is
    // on screen at startup rather than only after the first poll completes.
    public UrgentMessage? Current { get { Load(); return _pending.TryPeek(out var message) ? message : null; } }
    public int Count { get { Load(); return _pending.Count; } }
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
        Load();
        if (_seen.Contains(alert.Message.Id)) return false;
        if (_pending.Count >= 100) throw new InvalidOperationException("Urgent message queue is full; acknowledge a message and retry.");
        _seen.Add(alert.Message.Id);
        _pending.Enqueue(alert);
        MarkHandled(alert.Message.Id);
        Save();
        return true;
    }

    public void Acknowledge(string id)
    {
        if (Current?.Message.Id != id) return;
        _pending.Dequeue();
        // Written out immediately. This is the one action the user takes to say
        // "I have dealt with this", and the complaint it answers is the message
        // coming back the next time the app starts.
        _dirty = true;
        Save();
    }

    public async Task PollAsync(object context, CancellationToken cancellationToken)
    {
        if (_polling) return;
        _polling = true;
        Load();
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
                var previews = _pending.Where(alert => IsPreview(alert.Message.Id)).ToArray();
                if (previews.Length != _pending.Count) _dirty = true;
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
                MarkHandled(message.Id);
            }
            if (_cursor != batch.Cursor) _dirty = true;
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
        // Whatever happened, what was decided before it happened stays decided:
        // a batch that throws halfway must not re-pop the messages it already
        // got through.
        finally { Save(); _polling = false; }
    }

    // A local preview is a deliberate one-off "show me what this looks like",
    // not something to acknowledge again after every restart.
    private static bool IsPreview(string id) => id.StartsWith("preview:", StringComparison.Ordinal);

    private void MarkHandled(string id)
    {
        if (IsPreview(id) || !_processed.Add(id)) return;
        _handled.Add(id);
        _dirty = true;
    }

    private void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(_statePath)) return;
            var state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(_statePath));
            if (state is null) return;
            _cursor = string.IsNullOrWhiteSpace(state.Cursor) ? null : state.Cursor;
            foreach (var id in state.Handled ?? [])
            {
                if (string.IsNullOrWhiteSpace(id) || !_processed.Add(id)) continue;
                _seen.Add(id);
                _handled.Add(id);
            }
            // Alerts the user never acknowledged are still owed to them, so they
            // come back queued rather than gone. They are not re-analysed: their
            // ids are in the handled set and the AI has already had its say.
            foreach (var alert in state.Pending ?? [])
            {
                if (alert?.Message is null || string.IsNullOrWhiteSpace(alert.Message.Id)) continue;
                if (_pending.Count >= 100) break;
                _seen.Add(alert.Message.Id);
                _pending.Enqueue(alert);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            // Losing this file costs at most one repeat popup per message, so it
            // must never stop the service polling.
            logger?.LogError(ex, "Unable to read urgent message state from {Path}; handled messages may alert once more.", _statePath);
        }
    }

    private void Save()
    {
        if (!_dirty) return;
        try
        {
            if (_handled.Count > HandledLimit)
            {
                var excess = _handled.Count - HandledLimit;
                for (var i = 0; i < excess; i++)
                {
                    _processed.Remove(_handled[i]);
                    _seen.Remove(_handled[i]);
                }
                _handled.RemoveRange(0, excess);
            }
            var state = new PersistedState(_cursor, [.. _handled],
                [.. _pending.Where(alert => !IsPreview(alert.Message.Id))]);
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            // Temp file then move, so a crash mid-write cannot leave a truncated
            // file that throws away every id on the next start.
            File.WriteAllText(_statePath + ".tmp", JsonSerializer.Serialize(state));
            File.Move(_statePath + ".tmp", _statePath, true);
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            logger?.LogError(ex, "Unable to persist urgent message state to {Path}; handled messages may alert again after a restart.", _statePath);
        }
    }

    private sealed record PersistedState(string? Cursor, string[] Handled, UrgentMessage[] Pending);
    public sealed record MessageBatch(ExternalMessage[] Messages, string Cursor);
    public sealed record ExternalHealth(AdapterStatus[] Adapters);
    public sealed record AdapterStatus(string Name, bool Enabled, bool Connected);
    public sealed record MessageDecision(string MessageId, string Decision, string Reason);
}
