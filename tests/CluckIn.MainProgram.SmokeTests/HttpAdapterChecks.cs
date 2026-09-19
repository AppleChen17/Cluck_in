using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CluckIn.App.Models;
using CluckIn.App.Orchestration;

internal static class HttpAdapterChecks
{
    public static async Task RunAsync()
    {
        var requests = new List<string>();
        var logs = new List<string>();
        var message = new ExternalMessageContract {
            Id = "slack:C1:1", Source = "slack", Sender = "Teammate",
            Content = "Urgent", Timestamp = DateTimeOffset.UtcNow, Unread = true
        };
        var polls = 0;
        var failure = false;
        using var client = new HttpClient(new Handler(async (request, token) => {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add(path);
            if(path == "/health") return Json(new { status = "ok" });
            if(path == "/analyze-message") {
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
                Check(body.GetProperty("context").GetProperty("automationMode").GetString() == "on", "Automation mode missing");
                Check(body.GetProperty("context").GetProperty("currentTask").GetString() == "Task", "Task missing");
                return Json(new { messageId = message.Id, decision = "urgent", relevance = 1, urgency = 1,
                    requiresReply = true, replyDraft = "draft from analyze", reason = "urgent", metadata = (object?)null });
            }
            if(path == "/summarize-messages") {
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
                Check(body.GetProperty("messages")[0].GetProperty("id").GetString() == message.Id, "Summary envelope incorrect");
                return Json(new { summary = "summary", messageCount = 1 });
            }
            if(path == "/reply") {
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
                Check(body.EnumerateObject().Count() == 2 && body.GetProperty("messageId").GetString() == message.Id,
                    "Reply must reuse strict messageId/body API");
                return Json(new { delivered = false, dryRun = !failure, error = failure ? "provider failed" : null });
            }
            if(path == "/messages") {
                polls++;
                if(polls == 1) return Json(new { messages = new[] { message }, cursor = "opaque:1", hasMore = true });
                Check(request.RequestUri.Query.Contains("cursor=opaque%3A1"), "Cursor was not echoed");
                return Json(new { messages = Array.Empty<ExternalMessageContract>(), cursor = "opaque:2", hasMore = false });
            }
            throw new Exception("Unexpected HTTP round trip: " + path);
        })) { BaseAddress = new Uri("http://fixture/") };
        var ai = new FocusAIClient(client, () => "Task");
        ai.SetAutomationMode(AIAssistRoutingMode.On);
        await ai.StartAsync("s1", default);
        var decision = await ai.AnalyzeMessageAsync(message, "s1", default);
        Check(decision.RequiresReply, "requiresReply not mapped");
        Check(await ai.DraftReplyAsync(message, decision, "s1", default) == "draft from analyze", "Draft was not reused");
        Check(requests.SequenceEqual(["/health", "/analyze-message"]), "Draft triggered an extra request");
        Check(await ai.SummarizeAsync([new() { Message = message, Decision = decision,
            AnalysisTimestamp = DateTimeOffset.UtcNow }], "s1", default) == "summary", "Summary missing");
        await ai.StopAsync(default);
        var reply = new ExternalReplyClient(client, logs.Add);
        Check((await reply.SendReplyAsync(message, "draft", "s1:m1", default)).Success, "Dry run treated as failed");
        failure = true;
        Check(!(await reply.SendReplyAsync(message, "draft", "s1:m1", default)).Success, "HTTP 200 provider failure hidden");
        Check(logs.Any(log => log.Contains("provider failed")), "Send failure not logged");
        var listener = new ExternalMessageListener(client, logs.Add);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.MessageReceived += m => { Check(m.Id == message.Id, "Listener mapping"); received.TrySetResult(); return Task.CompletedTask; };
        await listener.StartAsync(default);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The second page is fetched immediately because hasMore is true.
        while(Volatile.Read(ref polls) < 2) await Task.Delay(10);
        await listener.StopAsync(default);
        await listener.StopAsync(default);
        Check(polls == 2, "Listener page traversal incorrect");
    }

    private static void Check(bool value, string message)
    {
        if(!value) throw new Exception(message);
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
