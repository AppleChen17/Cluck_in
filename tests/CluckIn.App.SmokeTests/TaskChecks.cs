using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows.Threading;
using CluckIn.App.Interfaces;
using CluckIn.App.Managers;
using CluckIn.App.Models;
using CluckIn.App.Repositories;
using CluckIn.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

static class TaskChecks
{
    public static async Task RunAsync()
    {
        var count = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException(label);
            count++;
        }
        var repository = new InMemoryTaskRepository();
        var profile = new TaskProfile
        {
            Id = "test", Name = "Test", Description = "Context",
            Apps = ["broken", "working", "working"], Urls = ["bad-url", "https://github.com"],
            AllowedApps = ["Code.exe"], AllowedDomains = ["github.com"], FocusDurationMinutes = 50
        };
        await repository.SaveTaskAsync(profile);
        profile.Apps.Clear();
        Check((await repository.GetTaskAsync("test"))!.Apps.Count == 3, "Repository owns saved lists");
        var session = new SessionManager();
        var whitelist = new WhitelistManager();
        var timer = new TimerManager();
        var focus = new FocusManager(timer);
        var desktop = new FakeDesktop(() =>
            Check(session.CurrentTask?.Id == "test" && whitelist.CurrentWhitelist?.Id == "test", "State and whitelist loaded before launch"));
        var manager = new TaskManager(desktop, session, whitelist, focus, repository, NullLogger<TaskManager>.Instance);
        await manager.StartTaskAsync("test");
        Check(desktop.Opened.SequenceEqual(["broken", "working", "bad-url", "https://github.com"]), "Apps before URLs, deduplication and failure isolation");
        Check(timer.GetCurrentSession() is { Status: FocusSessionStatus.Running, Duration.TotalMinutes: 50 }, "50 minute focus started after launches");
        await repository.SaveTaskAsync(new() { Id = "no-timer", Name = "No timer" });
        await manager.StartTaskAsync("no-timer");
        Check(timer.GetCurrentSession().Status == FocusSessionStatus.Stopped && whitelist.CurrentWhitelist!.AllowedDomains.Count == 0, "Switch replaces whitelist and stops old timer");
        try { await manager.StartTaskAsync("missing"); throw new Exception("Missing task accepted"); }
        catch (KeyNotFoundException) { count++; }
        Check(session.CurrentTask?.Id == "no-timer", "Unknown task preserves state");
        try { await repository.SaveTaskAsync(new() { Id = "invalid", Name = "Invalid", FocusDurationMinutes = 0 }); throw new Exception("Invalid timer accepted"); }
        catch (ArgumentException) { count++; }

        var realDesktop = new DesktopManager(NullLogger<DesktopManager>.Instance);
        try { await realDesktop.OpenApplicationAsync(@"C:\cluck-in-missing\missing.exe"); throw new Exception("Missing executable accepted"); }
        catch (System.IO.FileNotFoundException) { count++; }
        foreach (var url in new[] { "not-a-url", "file:///C:/Windows/notepad.exe", "javascript:alert(1)" })
        {
            try { await realDesktop.OpenUrlAsync(url); throw new Exception("Unsafe URL accepted"); }
            catch (ArgumentException) { count++; }
        }
        var rules = new WorkspaceProfile { Id = "domains", Name = "Domains", AllowedApplications = ["chrome"], AllowedDomains = ["github.com"] };
        FocusEvaluation Evaluate(string? url) => focus.Evaluate(new()
        {
            ActiveWindow = new() { ProcessName = "chrome" },
            Browser = new() { BrowserName = "Chrome", Url = url }
        }, rules);
        Check(Evaluate("https://docs.github.com/help").IsFocused, "Allowed subdomain");
        Check(!Evaluate("https://github.com.evil.example").IsFocused, "Domain suffix spoof rejected despite allowed browser app");
        Check(!Evaluate(null).IsEvaluated, "Unknown URL is neutral");
        Console.WriteLine($"Passed {count} Task checks.");
        await RunApiAsync();
    }

    private static Task RunApiAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    var mainAI = new MainAI();
                    var listener = new MainListener();
                    var notifications = new MainNotifications();
                    await using var app = TaskApiHost.Create(dispatcher, services => {
                        services.AddSingleton<IFocusAIEngine>(mainAI);
                        services.AddSingleton<IMessageListener>(listener);
                        services.AddSingleton<IUserNotificationService>(notifications);
                    });
                    app.Urls.Clear();
                    app.Urls.Add("http://127.0.0.1:0");
                    await app.StartAsync();
                    try
                    {
                        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
                        var demos = await client.GetFromJsonAsync<TaskProfile[]>("/api/tasks");
                        if (demos?.Single().Id != "task_001") throw new Exception("Demo missing");
                        var saved = await client.PutAsJsonAsync("/api/tasks/api-test", new TaskProfile
                        {
                            Id = "api-test", Name = "API Test", Apps = [@"C:\cluck-in-missing\missing.exe"],
                            Urls = ["invalid-url"], FocusDurationMinutes = 2, AllowedApps = ["Code.exe"]
                        });
                        saved.EnsureSuccessStatusCode();
                        var started = await client.PostAsJsonAsync("/api/tasks/api-test/start", new { });
                        started.EnsureSuccessStatusCode();
                        var result = await started.Content.ReadFromJsonAsync<StartResponse>();
                        if (result is not { Success: true, TaskId: "api-test", TaskName: "API Test" }) throw new Exception("Unexpected start response");
                        var agent = app.Services.GetRequiredService<DesktopAgentService>();
                        var context = await agent.GetContextAsync();
                        if (context.WorkspaceId != "api-test" || !context.TimerRunning) throw new Exception("API and desktop do not share state");
                        var intervention = await client.GetFromJsonAsync<InterventionState>("/api/intervention");
                        if (intervention is not { IsActive: false }) throw new Exception("Unexpected initial intervention");
                        var staleAction = await client.PostAsJsonAsync("/api/intervention/action", new InterventionActionRequest(Guid.NewGuid(), InterventionAction.TemporaryAllow));
                        if (staleAction.StatusCode != HttpStatusCode.Conflict) throw new Exception("Stale intervention action accepted");
                        var invalidAction = await client.PostAsJsonAsync("/api/intervention/action", new InterventionActionRequest(Guid.NewGuid(), InterventionAction.ShowIntervention));
                        if (invalidAction.StatusCode != HttpStatusCode.BadRequest) throw new Exception("Unsupported intervention action accepted");
                        var missing = await client.PostAsJsonAsync("/api/tasks/missing/start", new { });
                        if (missing.StatusCode != HttpStatusCode.NotFound || !(await missing.Content.ReadAsStringAsync()).Contains("Task not found")) throw new Exception("404 error missing");
                        var invalid = await client.PutAsJsonAsync("/api/tasks/bad", new TaskProfile { Id = "bad", Name = "Bad", FocusDurationMinutes = -1 });
                        if (invalid.StatusCode != HttpStatusCode.BadRequest) throw new Exception("Invalid configuration accepted");
                        var malformed = await client.PutAsync("/api/tasks/bad", new StringContent("{", System.Text.Encoding.UTF8, "application/json"));
                        if (malformed.StatusCode != HttpStatusCode.BadRequest || !(await malformed.Content.ReadAsStringAsync()).Contains("error")) throw new Exception("Malformed JSON error missing");
                        var form = await client.PostAsync("/api/tasks/api-test/start", new StringContent(""));
                        if (form.StatusCode != HttpStatusCode.UnsupportedMediaType) throw new Exception("Blind POST accepted");
                        var ended = await client.PostAsJsonAsync("/api/session/end-task", new { });
                        ended.EnsureSuccessStatusCode();
                        var afterEnd = await client.GetFromJsonAsync<SessionResponse>("/api/session");
                        if (afterEnd?.CurrentTask is not null || afterEnd?.FocusSession.Status != FocusSessionStatus.Stopped)
                            throw new Exception("End Task API must clear task and stop focus");
                        await CheckMainProgramApiAsync(client, agent, mainAI, listener, notifications);
                        client.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
                        var origin = await client.PostAsJsonAsync("/api/tasks/api-test/start", new { });
                        if (origin.StatusCode != HttpStatusCode.Forbidden) throw new Exception("Untrusted origin accepted");
                        Console.WriteLine("Passed 13 Task/Intervention API integration checks (no real app or URL launched).");
                    }
                    finally { await app.StopAsync(); }
                    completion.SetResult();
                }
                catch (Exception exception) { completion.SetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static async Task CheckMainProgramApiAsync(HttpClient client, DesktopAgentService agent,
        MainAI ai, MainListener listener, MainNotifications notifications)
    {
        async Task Input(string type, object payload, int key) {
            using var result = await client.PostAsJsonAsync("/input-event",
                new { type, source = "logitech", timestamp = DateTimeOffset.UtcNow, payload, metadata = new { keyId = key } });
            result.EnsureSuccessStatusCode();
        }
        agent.SetActiveWorkspace("coding");
        await Input("CHANGE_MODE", new { mode = "focus" }, 1);
        await Input("CHANGE_MODE", new { mode = "focus" }, 1);
        if(ai.Starts != 1 || listener.Starts != 1 || (await agent.GetContextAsync()).TimerRunning)
            throw new Exception("Focus mode must start orchestration once without starting the timer");
        await Input("SET_AI_ASSIST_MODE", new { mode = "suggestion" }, 3);
        if(agent.MainProgram!.CurrentAIAssistMode != AIAssistRoutingMode.Suggestion)
            throw new Exception("AI routing mode not wired");
        using var pat = await client.PostAsJsonAsync("/api/main/pat", new { });
        pat.EnsureSuccessStatusCode();
        if(notifications.Summaries != 1) throw new Exception("PAT endpoint not wired");
        await Input("START_FOCUS", new { focusDurationSeconds = 60 }, 5);
        if(!(await agent.GetContextAsync()).TimerRunning || ai.Starts != 1) throw new Exception("START_FOCUS lifecycle regressed");
        await Input("PAUSE_FOCUS", new { }, 5);
        if((await agent.GetContextAsync()).FocusSession.Status != FocusSessionStatus.Paused) throw new Exception("Pause not wired");
        await Input("RESUME_FOCUS", new { }, 5);
        if(!(await agent.GetContextAsync()).TimerRunning) throw new Exception("Resume not wired");
        await Input("STOP_FOCUS", new { }, 6);
        if((await agent.GetContextAsync()).TimerRunning || !agent.MainProgram.FocusSessionActive)
            throw new Exception("STOP_FOCUS must stop only timer");
        await Input("CHANGE_MODE", new { mode = "idle" }, 1);
        await Input("CHANGE_MODE", new { mode = "idle" }, 1);
        if(ai.Stops != 1 || listener.Stops != 1 || agent.MainProgram.FocusSessionActive)
            throw new Exception("Idle cleanup not idempotent");
        await Input("START_FOCUS", new { focusDurationSeconds = 60 }, 5);
        if(ai.Starts != 1) throw new Exception("Timer start incorrectly enters Main Focus lifecycle");
        await Input("CHANGE_MODE", new { mode = "focus" }, 1);
        await agent.ShutdownAsync();
        await agent.ShutdownAsync();
        if(ai.Stops != 2 || listener.Stops != 2 || agent.MainProgram.FocusSessionActive ||
            (await agent.GetContextAsync()).TimerRunning) throw new Exception("Desktop shutdown cleanup failed");
        Console.WriteLine("Passed 10 Main Program API wiring checks.");
    }

    private sealed class MainAI : IFocusAIEngine
    {
        public int Starts, Stops;
        public Task StartAsync(string id, CancellationToken token) { Starts++; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken token) { Stops++; return Task.CompletedTask; }
        public Task<AIDecisionContract> AnalyzeMessageAsync(ExternalMessageContract message, string id, CancellationToken token) =>
            throw new NotSupportedException();
        public Task<string?> DraftReplyAsync(ExternalMessageContract message, AIDecisionContract decision, string id, CancellationToken token) =>
            throw new NotSupportedException();
        public Task<string> SummarizeAsync(IReadOnlyList<FocusMessageRecord> messages, string id, CancellationToken token) =>
            throw new NotSupportedException();
    }
    private sealed class MainListener : IMessageListener
    {
        public int Starts, Stops;
        public event Func<ExternalMessageContract, Task>? MessageReceived { add { } remove { } }
        public Task StartAsync(CancellationToken token) { Starts++; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken token) { Stops++; return Task.CompletedTask; }
    }
    private sealed class MainNotifications : IUserNotificationService
    {
        public int Summaries;
        public Task ShowUrgentMessageAsync(ExternalMessageContract message, CancellationToken token) => throw new NotSupportedException();
        public Task ShowUrgentMessageWithSuggestionAsync(ExternalMessageContract message, string suggestion, CancellationToken token) =>
            throw new NotSupportedException();
        public Task ShowSummaryAsync(string summary, bool isEmpty, CancellationToken token) { Summaries++; return Task.CompletedTask; }
    }

    private sealed record SessionResponse(TaskProfile? CurrentTask, FocusSession FocusSession);
    private sealed record StartResponse(bool Success, string TaskId, string TaskName);

    private sealed class FakeDesktop(Action beforeLaunch) : IDesktopManager
    {
        public Task<bool> ReturnToWorkAsync(DesktopContext context) => Task.FromResult(true);
        public List<string> Opened { get; } = [];
        public Task OpenApplicationAsync(string path) => Open(path);
        public Task OpenUrlAsync(string url) => Open(url);
        private Task Open(string target)
        {
            beforeLaunch();
            Opened.Add(target);
            if (target is "broken" or "bad-url") throw new InvalidOperationException("Simulated launch failure");
            return Task.CompletedTask;
        }
    }
}
