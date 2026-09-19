using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
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
        var frontendDesktop = new FakeDesktop(() => { });
        var inputHandler = new InputEventHandler(manager, frontendDesktop);
        await inputHandler.HandleAsync(new()
        {
            Type = "SHOW_TASK_SELECTION", Source = "logitech", Timestamp = DateTimeOffset.Now,
            Payload = JsonSerializer.SerializeToElement(new { })
        });
        Check(frontendDesktop.Opened.SequenceEqual(["http://localhost:5173/#tasks"]) &&
            session.CurrentTask is null && desktop.Opened.Count == 0,
            "Opening frontend task UI does not start a task or launch task resources");
        await inputHandler.HandleAsync(new()
        {
            Type = "SELECT_TASK", Source = "logitech", Timestamp = DateTimeOffset.Now,
            Payload = JsonSerializer.SerializeToElement(new { taskId = "test" })
        });
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
                    var desktop = new FakeDesktop(() =>
                    {
                        if (!dispatcher.CheckAccess()) throw new Exception("Launch must run on dispatcher");
                    });
                    await using var app = TaskApiHost.Create(dispatcher,
                        services => services.AddSingleton<IDesktopManager>(desktop));
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
                        desktop.Opened.Clear();
                        object Input(string type, object payload) => new { type, source = "logitech", timestamp = DateTimeOffset.Now, payload };
                        var showPicker = await client.PostAsJsonAsync("/input-event", Input("SHOW_TASK_SELECTION", new { }));
                        showPicker.EnsureSuccessStatusCode();
                        if (!desktop.Opened.SequenceEqual(["http://localhost:5173/#tasks"]) ||
                            agent.CurrentTask?.Id != "api-test" || !((await agent.GetContextAsync()).TimerRunning))
                            throw new Exception("Frontend request must open only Task UI and preserve task/timer state");
                        (await client.PutAsJsonAsync("/api/tasks/console-test", new TaskProfile { Id = "console-test", Name = "Console Test" })).EnsureSuccessStatusCode();
                        var selected = await client.PostAsJsonAsync("/input-event", Input("SELECT_TASK", new { taskId = "console-test" }));
                        selected.EnsureSuccessStatusCode();
                        var consoleSession = await client.GetFromJsonAsync<SessionResponse>("/api/session");
                        if (consoleSession?.CurrentTask?.Id != "console-test" || agent.CurrentTask?.Id != "console-test" ||
                            consoleSession.FocusSession.Status != FocusSessionStatus.Stopped)
                            throw new Exception("Console selection did not share task/session state or stop old timer");
                        foreach (var payload in new object[] { new { }, new { taskId = "" }, new { taskId = 42 }, new[] { "invalid" } })
                        {
                            var badInput = await client.PostAsJsonAsync("/input-event", Input("SELECT_TASK", payload));
                            if (badInput.StatusCode != HttpStatusCode.BadRequest) throw new Exception("Invalid task input accepted");
                        }
                        var missingInput = await client.PostAsJsonAsync("/input-event", Input("SELECT_TASK", new { taskId = "missing" }));
                        if (missingInput.StatusCode != HttpStatusCode.NotFound || agent.CurrentTask?.Id != "console-test")
                            throw new Exception("Missing input task must preserve current task");
                        var unsupportedInput = await client.PostAsJsonAsync("/input-event", Input("UNKNOWN", new { }));
                        if (unsupportedInput.StatusCode != HttpStatusCode.BadRequest) throw new Exception("Unsupported event accepted");
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
                        client.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
                        var origin = await client.PostAsJsonAsync("/api/tasks/api-test/start", new { });
                        if (origin.StatusCode != HttpStatusCode.Forbidden) throw new Exception("Untrusted origin accepted");
                        var inputOrigin = await client.PostAsJsonAsync("/input-event", Input("SELECT_TASK", new { taskId = "console-test" }));
                        if (inputOrigin.StatusCode != HttpStatusCode.Forbidden) throw new Exception("Untrusted input origin accepted");
                        Console.WriteLine("Passed Task/InputEvent/Intervention API integration checks (no real app or URL launched).");
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
