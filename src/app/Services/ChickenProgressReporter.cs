using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using CluckIn.App.Managers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CluckIn.App.Services;

// Durable delivery receipts, not a second inventory or cumulative counter store.
public sealed class ChickenProgressReporter(ILogger<ChickenProgressReporter> logger) : BackgroundService
{
    private readonly object _gate = new();
    private readonly string _path = Environment.GetEnvironmentVariable("CLUCKIN_FOCUS_OUTBOX_PATH") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CluckIn", "focus-outbox.json");
    private Dictionary<string, FocusProgress>? _pending;

    private Dictionary<string, FocusProgress> Pending => _pending ??=
        File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<string, FocusProgress>>(File.ReadAllText(_path))
                ?? throw new InvalidDataException("Invalid focus outbox")
            : new();

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(Pending));
        File.Move(_path + ".tmp", _path, true);
    }

    public void Record(FocusProgress progress)
    {
        lock (_gate)
        {
            try { Pending[progress.SessionId] = progress; Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                logger.LogError(ex, "Unable to persist focus progress; timer controls remain available.");
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri(Environment.GetEnvironmentVariable("CLUCKIN_CHICKEN_URL") ?? "http://127.0.0.1:8001/"),
            Timeout = TimeSpan.FromSeconds(2)
        };
        var unavailable = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                FocusProgress[] pending;
                lock (_gate) pending = Pending.Values.ToArray();
                foreach (var progress in pending)
                {
                    using var response = await client.PostAsJsonAsync("event", new
                    {
                        type = "RECORD_FOCUS",
                        payload = new { sessionId = progress.SessionId, seconds = progress.Seconds, completed = progress.Completed }
                    }, stoppingToken);
                    response.EnsureSuccessStatusCode();
                    lock (_gate)
                    {
                        if (Pending.TryGetValue(progress.SessionId, out var latest) && latest == progress)
                        {
                            Pending.Remove(progress.SessionId);
                            Save();
                        }
                    }
                }
                unavailable = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (!unavailable) logger.LogWarning(ex, "Chicken progress delivery pending; will retry.");
                unavailable = true;
            }
            try { await Task.Delay(1000, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
