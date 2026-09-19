using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CluckIn.App.Models;
using CluckIn.App.Services;
using CluckIn.App.Views;

internal static class UrgentMessageChecks
{
    public static async Task RunAsync()
    {
        var count = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            count++;
        }
        var factory = new FakeClients();
        var service = new UrgentMessageService(factory);
        ExternalMessage Message(string id) => new()
        {
            Id = id, Source = "slack", Sender = "Demo teammate", Title = "Production build failed",
            Content = "正式環境部署失敗，請立即協助確認。", Timestamp = DateTimeOffset.Now
        };
        var first = new UrgentMessage(Message("1"), "Deployment is blocked and requires immediate attention.");
        Check(service.Enqueue(first), "First alert accepted");
        Check(!service.Enqueue(first), "Duplicate active alert ignored");
        service.Enqueue(new(Message("2"), "Urgent follow-up"));
        service.Acknowledge("stale");
        Check(service.Count == 2, "Stale acknowledgement cannot dismiss another alert");
        service.Acknowledge("1");
        Check(service.Current?.Message.Id == "2", "Acknowledging advances queue");
        Check(!service.Enqueue(first), "Acknowledged message does not reappear");
        service.Acknowledge("2");
        Check(service.Current is null, "Queue empties");
        try { service.Enqueue(new(Message(""), "reason")); throw new Exception("Invalid message accepted"); }
        catch (ArgumentException) { count++; }

        factory.Messages = [Message("urgent"), Message("normal"), Message("held")];
        await service.PollAsync(new { mode = "focus" }, CancellationToken.None);
        Check(service.Count == 1 && service.Current?.Message.Id == "urgent", "Only urgent decisions display");
        Check(factory.SawFocusContext, "Analysis receives session context");
        await service.PollAsync(new { mode = "focus" }, CancellationToken.None);
        Check(factory.AnalysisCalls == 3 && service.Count == 1, "Replayed messages do not repeat analysis or alert");
        Check(factory.LastMessagePath?.Contains("cursor=cursor-1") == true, "Poll advances opaque cursor");
        factory.Messages = [Message("retry")];
        factory.FailAnalysis = true;
        await service.PollAsync(new { mode = "focus" }, CancellationToken.None);
        Check(service.Status.Contains("unavailable"), "Analysis failure exposed");
        Check(service.Status.Contains("AI service") && service.Status.Contains("analyze-message") && service.Status.Contains("503"),
            "Failure identifies API stage, endpoint and HTTP status");
        factory.FailAnalysis = false;
        await service.PollAsync(new { mode = "focus" }, CancellationToken.None);
        Check(service.Count == 2, "Failed message retried successfully");
        factory.FixtureMode = true;
        var callsBeforeFixture = factory.AnalysisCalls;
        await service.PollAsync(new { mode = "focus" }, CancellationToken.None);
        Check(service.Count == 0 && service.Status.Contains("fixture") && factory.AnalysisCalls == callsBeforeFixture,
            "Fixture service clears pending alerts and never triggers automatic AI notifications");
        service.Enqueue(new(Message("preview:test"), "Explicit local preview"));
        await service.PollAsync(new { mode = "focus" }, CancellationToken.None);
        Check(service.Current?.Message.Id == "preview:test", "Fixture check preserves explicitly requested preview");
        await RenderAsync(first);
        Console.WriteLine($"Passed {count} urgent message checks and offscreen WPF rendering (no external services).");
    }

    private static Task RenderAsync(UrgentMessage message)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var window = new UrgentMessageWindow
                {
                    DataContext = new { UrgentMessage = message, UrgentMessageCount = 2 }
                };
                if (!window.Topmost || window.ShowActivated || window.ShowInTaskbar)
                    throw new Exception("Urgent window must be topmost without activating or adding a taskbar item.");
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(440, 650));
                content.Arrange(new Rect(0, 0, 440, content.DesiredSize.Height));
                content.UpdateLayout();
                var bitmap = new RenderTargetBitmap(440, (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                    drawing.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, 440, content.ActualHeight));
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory("TestResults/wpf");
                using var stream = File.Create("TestResults/wpf/urgent-message.png");
                encoder.Save(stream);
                window.Close();
                completion.SetResult();
            }
            catch (Exception exception) { completion.SetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed class FakeClients : IHttpClientFactory
    {
        public ExternalMessage[] Messages = [];
        public bool FailAnalysis;
        public bool FixtureMode;
        public int AnalysisCalls;
        public bool SawFocusContext;
        public string? LastMessagePath;
        public HttpClient CreateClient(string name) => new(new Handler(this)) { BaseAddress = new Uri("http://localhost/") };
        private sealed class Handler(FakeClients owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                object payload;
                if (request.RequestUri!.AbsolutePath == "/health")
                {
                    payload = new { adapters = new[] { new { name = owner.FixtureMode ? "fixture" : "slack", enabled = true, connected = true } } };
                }
                else if (request.Method == HttpMethod.Get)
                {
                    owner.LastMessagePath = request.RequestUri!.PathAndQuery;
                    payload = new { messages = owner.Messages, cursor = "cursor-1" };
                }
                else
                {
                    owner.AnalysisCalls++;
                    if (owner.FailAnalysis) return new(HttpStatusCode.ServiceUnavailable);
                    using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                    var id = document.RootElement.GetProperty("message").GetProperty("id").GetString();
                    owner.SawFocusContext = document.RootElement.GetProperty("context").GetProperty("mode").GetString() == "focus";
                    payload = new { messageId = id, decision = id == "normal" ? "allow" : id == "held" ? "hold" : "urgent", reason = "Immediate attention required" };
                }
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json")
                };
            }
        }
    }
}
