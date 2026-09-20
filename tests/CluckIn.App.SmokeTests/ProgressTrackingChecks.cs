using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CluckIn.App.Services;
using Microsoft.Extensions.Logging.Abstractions;
using CluckIn.App.Managers;
using CluckIn.App.Models;

internal static class ProgressTrackingChecks
{
    public static void Run()
    {
        var clock = new Clock();
        var observations = new List<FocusProgress>();
        var timer = new ProgressTrackingTimer(new TimerManager(clock), observations.Add);
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        timer.Start(TimeSpan.FromSeconds(10));
        var start = timer.GetCurrentSession().StartTime;
        clock.Advance(3);
        timer.Pause();
        Check(observations.Last().Seconds == 3 && !observations.Last().Completed, "Actual focused seconds");
        clock.Advance(100);
        timer.GetCurrentSession();
        Check(observations.Last().Seconds == 3, "Paused time excluded");
        timer.Resume(); clock.Advance(2); timer.Stop();
        Check(observations.Last().Seconds == 5 && !observations.Last().Completed, "Early stop counts actual time without reward");
        Check(timer.GetCurrentSession().Status == FocusSessionStatus.Stopped, "Stop behavior preserved");
        var firstId = observations.Last().SessionId;
        timer.Start(TimeSpan.FromSeconds(4));
        clock.Advance(5);
        Check(timer.GetCurrentSession().Status == FocusSessionStatus.Completed, "Natural completion preserved");
        Check(observations.Last().Seconds == 4 && observations.Last().Completed, "Completion capped to duration");
        Check(observations.Last().SessionId != firstId, "Independent session receipt IDs");
        var count = observations.Count;
        timer.GetCurrentSession(); timer.GetCurrentSession();
        Check(observations.Count == count, "Repeated reads do not publish duplicates");
        Check(start is not null, "Timer start metadata preserved");
        Console.WriteLine($"Passed {checks} focus-progress observer checks.");
    }
    public static async Task CheckDeliveryAsync()
    {
        var previousUrl = Environment.GetEnvironmentVariable("CLUCKIN_CHICKEN_URL");
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var url = $"http://127.0.0.1:{port}/";
        Environment.SetEnvironmentVariable("CLUCKIN_CHICKEN_URL", url);
        using var listener = new HttpListener(); listener.Prefixes.Add(url); listener.Start();
        var progress = new FocusProgress("delivery-test", 4, true);
        using (var original = new ChickenProgressReporter(NullLogger<ChickenProgressReporter>.Instance)) original.Record(progress);
        var path = Environment.GetEnvironmentVariable("CLUCKIN_FOCUS_OUTBOX_PATH")!;
        if (!File.ReadAllText(path).Contains("delivery-test")) throw new Exception("Outbox not persisted");
        using var restored = new ChickenProgressReporter(NullLogger<ChickenProgressReporter>.Instance);
        try
        {
            await restored.StartAsync(CancellationToken.None);
            string? first = null;
            foreach (var status in new[] { 503, 200 })
            {
                var request = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5));
                using var reader = new StreamReader(request.Request.InputStream);
                var body = await reader.ReadToEndAsync();
                var data = JsonDocument.Parse(body).RootElement;
                if (data.GetProperty("type").GetString() != "RECORD_FOCUS" ||
                    data.GetProperty("payload").GetProperty("sessionId").GetString() != "delivery-test")
                    throw new Exception("Invalid progress delivery");
                if (first is not null && body != first) throw new Exception("Retry changed receipt");
                first = body;
                request.Response.StatusCode = status; request.Response.Close();
            }
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (File.ReadAllText(path) != "{}" && DateTime.UtcNow < deadline) await Task.Delay(20);
            if (File.ReadAllText(path) != "{}") throw new Exception("Acknowledged receipt retained");
            Console.WriteLine("Passed focus outbox restart, HTTP retry, stable receipt and acknowledgement checks.");
        }
        finally
        {
            await restored.StopAsync(CancellationToken.None);
            Environment.SetEnvironmentVariable("CLUCKIN_CHICKEN_URL", previousUrl);
        }
    }

    public static async Task CheckProductionPathAsync()
    {
        var previousUrl = Environment.GetEnvironmentVariable("CLUCKIN_CHICKEN_URL");
        var previousOutbox = Environment.GetEnvironmentVariable("CLUCKIN_FOCUS_OUTBOX_PATH");
        var directory = Path.Combine(Path.GetTempPath(), $"cluckin-live-path-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var statePath = Path.Combine(directory, "chicken-state.json");
        var outboxPath = Path.Combine(directory, "focus-outbox.json");
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var url = $"http://127.0.0.1:{port}/";
        var root = Directory.GetCurrentDirectory();
        var python = Path.Combine(root, ".venv", "Scripts", "python.exe");
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-B", "-m", "uvicorn", "api:app", "--app-dir", "src/chicken",
                     "--host", "127.0.0.1", "--port", port.ToString(), "--log-level", "error" })
            start.ArgumentList.Add(argument);
        start.Environment["CLUCKIN_CHICKEN_STATE_PATH"] = statePath;
        using var api = Process.Start(start) ?? throw new Exception("Unable to start chicken API.");
        using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(2) };
        ChickenProgressReporter? reporter = null;
        try
        {
            var readyBy = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                try { if ((await http.GetAsync("health")).IsSuccessStatusCode) break; } catch { }
                if (DateTime.UtcNow >= readyBy) throw new Exception("Chicken API did not start.");
                await Task.Delay(50);
            }
            Environment.SetEnvironmentVariable("CLUCKIN_CHICKEN_URL", url);
            Environment.SetEnvironmentVariable("CLUCKIN_FOCUS_OUTBOX_PATH", outboxPath);
            reporter = new ChickenProgressReporter(NullLogger<ChickenProgressReporter>.Instance);
            await reporter.StartAsync(CancellationToken.None);
            var clock = new Clock();
            var timer = new ProgressTrackingTimer(new TimerManager(clock), reporter.Record);

            async Task<JsonElement> AwaitState(long seconds, long feeds, long rewards)
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        var view = await http.GetFromJsonAsync<JsonElement>("view");
                        if (view.GetProperty("totalFocusSeconds").GetInt64() == seconds &&
                            view.GetProperty("feedCount").GetInt64() == feeds && File.Exists(statePath))
                        {
                            var state = JsonDocument.Parse(await File.ReadAllTextAsync(statePath)).RootElement;
                            if (state.GetProperty("focusFeedRewardsGranted").GetInt64() == rewards) return view;
                        }
                    }
                    catch { }
                    await Task.Delay(50);
                }
                throw new Exception($"Production focus path did not reach {seconds}s/{feeds} feed/{rewards} rewards.");
            }

            timer.Start(TimeSpan.FromSeconds(30)); clock.Advance(5); timer.Stop();
            await AwaitState(5, 1, 1);
            timer.Start(TimeSpan.FromSeconds(30)); clock.Advance(5); timer.Stop();
            await AwaitState(10, 2, 2);
            Console.WriteLine("Passed production C# timer -> reporter -> real chicken API -> persisted view checks.");
        }
        finally
        {
            if (reporter is not null) { await reporter.StopAsync(CancellationToken.None); reporter.Dispose(); }
            if (!api.HasExited) { api.Kill(entireProcessTree: true); api.WaitForExit(); }
            Environment.SetEnvironmentVariable("CLUCKIN_CHICKEN_URL", previousUrl);
            Environment.SetEnvironmentVariable("CLUCKIN_FOCUS_OUTBOX_PATH", previousOutbox);
            Directory.Delete(directory, true);
        }
    }

    private sealed class Clock : TimeProvider
    {
        private long _seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => _seconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(_seconds);
        public void Advance(long seconds) => _seconds += seconds;
    }
}
