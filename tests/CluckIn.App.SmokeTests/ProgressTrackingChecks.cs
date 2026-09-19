using System.IO;
using System.Net;
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

    private sealed class Clock : TimeProvider
    {
        private long _seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => _seconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(_seconds);
        public void Advance(long seconds) => _seconds += seconds;
    }
}
