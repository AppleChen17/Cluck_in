using System.Net;
using System.Net.Http;
using System.Text.Json;
using CluckIn.App.Interfaces;
using CluckIn.App.Managers;
using CluckIn.App.Models;
using CluckIn.App.Services;

static class TaskAnalysisChecks
{
    public static async Task RunAsync()
    {
        var count = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); count++; }
        var clock = new ManualClock();
        var timer = new TimerManager(clock);
        timer.Start(TimeSpan.FromMinutes(25));
        var session = new SessionManager();
        var task = new TaskProfile { Id = "task", Name = "Development", Description = "Build WPF", AllowedApps = ["code"], AllowedDomains = ["github.com"] };
        session.SetCurrentTask(task);
        var whitelist = new WhitelistManager(); whitelist.Load(task);
        var intervention = new InterventionManager(new FakeDesktop(), clock);
        var client = new FakeAi();
        var focus = new FocusManager(timer, whitelist, intervention);
        var contexts = new FakeContext();
        var agent = new DesktopAgentService(contexts, new WorkspaceManager(), focus, timer, session, whitelist, intervention, client, aiOptions: Microsoft.Extensions.Options.Options.Create(new AiEngineOptions { TimeoutSeconds = 1 }), timeProvider: clock);
        DesktopContext Snapshot(string app, string? url = null, string title = "Tutorial") => new()
        {
            WorkspaceId = session.CurrentTask!.Id, FocusModeEnabled = true, TimerRunning = true,
            FocusSession = timer.GetCurrentSession(), ActiveWindow = new() { ProcessName = app, ProcessId = 1234, WindowHandle = 42, WindowTitle = title },
            Browser = app == "chrome" ? new() { BrowserName = "Chrome", Url = url, PageTitle = title } : null
        };
        var work = Snapshot("code");
        Check((await agent.EvaluateFocusAsync(work)).IsFocused && client.Calls == 0, "Whitelist bypasses AI");
        var web = Snapshot("chrome", "https://youtube.com/watch?v=1");
        Check((await agent.EvaluateFocusAsync(web)) is { IsFocused: true, Source: "ai" }, "AI allows non-whitelisted web");
        Check(!intervention.State.IsActive && !whitelist.CurrentWhitelist!.AllowedDomains.Contains("youtube.com"), "AI allow is not permanent");
        Check(client.Last!.Target.Type == "web" && client.Last.Target.Url == web.Browser!.Url && client.Last.Context.CurrentTask == "Development: Build WPF", "Web and task mapping");
        for (var i = 0; i < 30; i++) await agent.EvaluateFocusAsync(web);
        Check(client.Calls == 1, "Repeated polling uses cache");
        clock.Advance(TimeSpan.FromSeconds(46)); await agent.EvaluateFocusAsync(web);
        Check(client.Calls == 2, "Cache expires");
        client.Decision = "block";
        var game = Snapshot("game");
        await agent.EvaluateFocusAsync(game);
        Check(client.Last!.Target is { Type: "app", Identifier: "game", Url: null }, "Application mapping");
        clock.Advance(InterventionManager.GracePeriod); await agent.EvaluateFocusAsync(game);
        Check(intervention.State.IsActive, "AI block follows grace period");
        contexts.Value = game;
        await agent.HandleInterventionAsync(new(intervention.State.Id!.Value, InterventionAction.TemporaryAllow));
        var calls = client.Calls;
        Check((await agent.EvaluateFocusAsync(game)).Source == "temporary_allow" && client.Calls == calls && !intervention.State.IsActive, "Temporary allow bypasses AI");
        session.SetCurrentTask(task with { Id = "second" }); whitelist.Load(session.CurrentTask!);
        game = Snapshot("game"); await agent.EvaluateFocusAsync(game);
        Check(client.Calls == calls + 1, "Task change invalidates allowance and analysis");
        client.Fail = true;
        var failed = Snapshot("other");
        Check((await agent.EvaluateFocusAsync(failed)) is { Source: "fallback", IsFocused: false }, "AI exception falls back");
        clock.Advance(InterventionManager.GracePeriod); await agent.EvaluateFocusAsync(failed);
        Check(intervention.State.IsActive, "Fallback retains intervention behavior");
        calls = client.Calls; await agent.EvaluateFocusAsync(failed);
        Check(client.Calls == calls, "Failure backoff prevents request storm");
        client.Fail = false; client.Decision = "warn";
        var warning = Snapshot("warning"); await agent.EvaluateFocusAsync(warning);
        clock.Advance(InterventionManager.GracePeriod); await agent.EvaluateFocusAsync(warning);
        Check(!intervention.State.IsActive && (await agent.EvaluateFocusAsync(warning)).IsFocused, "Warn allows without chicken");
        client.Decision = "allow";
        var unknown = Snapshot("chrome");
        await agent.EvaluateFocusAsync(unknown);
        Check(client.Last!.Target is { Type: "web", Url: null }, "Missing URL sent as null");
        var pending = new TaskCompletionSource<TaskDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Pending = pending.Task;
        var old = Snapshot("slow-app");
        Check((await agent.EvaluateFocusAsync(old)).Source == "ai_pending", "Slow AI does not block poll");
        calls = client.Calls; await agent.EvaluateFocusAsync(old);
        Check(client.Calls == calls, "Single in-flight request for context");
        await agent.EvaluateFocusAsync(work with { WorkspaceId = "second" });
        pending.SetResult(new() { Decision = "block", Reason = "old", Relevance = 0 });
        await Task.Delay(10);
        Check((await agent.EvaluateFocusAsync(work with { WorkspaceId = "second" })).IsFocused && !intervention.State.IsActive, "Late result cannot affect new context");
        client.Pending = null;
        await agent.EvaluateFocusAsync(Snapshot("pause-test"));
        calls = client.Calls;
        await agent.EvaluateFocusAsync(Snapshot("pause-test") with { TimerRunning = false });
        Check(client.Calls == calls && !intervention.State.IsActive, "Pause skips AI and clears prompt");
        client.Pending = new TaskCompletionSource<TaskDecision>().Task;
        var timeoutContext = Snapshot("timeout");
        Check((await agent.EvaluateFocusAsync(timeoutContext)).Source == "ai_pending", "Timeout request begins without blocking");
        await Task.Delay(1150);
        Check((await agent.EvaluateFocusAsync(timeoutContext)).Source == "fallback", "Bounded timeout falls back without Python");
        client.Pending = null;
        calls = client.Calls;
        session.ClearCurrentTask();
        await agent.EvaluateFocusAsync(game);
        Check(client.Calls == calls, "No task and no matching workspace skips AI");

        var handler = new StubHandler();
        var httpClient = new TaskAnalysisClient(new FakeFactory(handler));
        var request = TaskAnalysisMapper.Map(task, web, whitelist.CurrentWhitelist!);
        Check((await httpClient.AnalyzeAsync(request)).Decision == "allow", "HTTP response parsed");
        Check(handler.Method == HttpMethod.Post && handler.Path == "/analyze-task", "HTTP endpoint contract");
        using (var json = JsonDocument.Parse(handler.Body!))
            Check(json.RootElement.GetProperty("target").GetProperty("type").GetString() == "web" &&
                json.RootElement.GetProperty("context").GetProperty("currentTask").ValueKind == JsonValueKind.String, "Camel-case JSON contract");
        foreach (var body in new[] { "{}", "not-json", "{\"decision\":\"hold\",\"relevance\":0.5,\"reason\":\"x\"}", "{\"decision\":\"allow\",\"relevance\":2,\"reason\":\"x\"}" })
        {
            handler.BodyResponse = body;
            try { await httpClient.AnalyzeAsync(request); throw new Exception("Invalid response accepted"); }
            catch (JsonException) { count++; }
        }
        handler.Status = HttpStatusCode.InternalServerError;
        try { await httpClient.AnalyzeAsync(request); throw new Exception("HTTP 500 accepted"); }
        catch (HttpRequestException) { count++; }
        Console.WriteLine($"Passed {count} Task Analysis integration/client checks (no Python required).");
        await CheckWorkspaceAsync();
    }
    private static async Task CheckWorkspaceAsync()
    {
        var count = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); count++; }
        var clock = new ManualClock();
        var timer = new TimerManager(clock);
        var session = new SessionManager();
        var rules = new WhitelistManager();
        var workspaces = new WorkspaceManager();
        workspaces.AddWorkspace(new() { Id = "study", Name = "Study", Description = "Study history", AllowedApplications = ["reader"] });
        var client = new FakeAi();
        var intervention = new InterventionManager(new FakeDesktop(), clock);
        var contexts = new FakeContext();
        var focus = new FocusManager(timer, rules, intervention);
        var agent = new DesktopAgentService(contexts, workspaces, focus, timer, session, rules, intervention, client, timeProvider: clock);
        agent.StartFocus("coding", TimeSpan.FromMinutes(25));
        DesktopContext Snapshot(string app = "chrome") => new()
        {
            WorkspaceId = session.CurrentTask?.Id ?? workspaces.GetActiveWorkspace()?.Id,
            FocusModeEnabled = true, TimerRunning = true, FocusSession = timer.GetCurrentSession(),
            ActiveWindow = new() { ProcessName = app, ProcessId = 1234, WindowHandle = 42, WindowTitle = app == "chrome" ? "FastAPI Documentation" : "Request editor" },
            Browser = app == "chrome" ? new() { BrowserName = "Chrome", PageTitle = "FastAPI Documentation", Url = "https://fastapi.tiangolo.com/tutorial/body/" } : null
        };
        var result = await agent.EvaluateFocusAsync(Snapshot("code"));
        Check(result.IsFocused && client.Calls == 0, "Workspace whitelist bypasses AI");
        result = await agent.EvaluateFocusAsync(Snapshot());
        Check(result is { IsFocused: true, AiContextSource: "workspace" } && !intervention.State.IsActive, "Workspace AI allow");
        Check(session.CurrentTask is null, "Workspace AI does not fabricate task");
        Check(client.Last!.Context.CurrentTask!.StartsWith("Workspace: Coding. Software development and technical work") &&
            client.Last.Context.CurrentTask.Contains("github.com"), "Workspace description and rules mapped");
        var calls = client.Calls;
        await agent.EvaluateFocusAsync(Snapshot());
        Check(client.Calls == calls, "Workspace same-context cache hit");
        client.Decision = "warn";
        result = await agent.EvaluateFocusAsync(Snapshot("Postman"));
        clock.Advance(InterventionManager.GracePeriod);
        await agent.EvaluateFocusAsync(Snapshot("Postman"));
        Check(result.IsFocused && !intervention.State.IsActive && client.Last.Target.Type == "app", "Workspace app warn never enters intervention");
        client.Decision = "block";
        agent.SetActiveWorkspace("study");
        calls = client.Calls;
        var study = Snapshot();
        result = await agent.EvaluateFocusAsync(study);
        Check(client.Calls == calls + 1 && result.AiContextSource == "workspace" && client.Last.Context.CurrentTask!.StartsWith("Workspace: Study."), "Workspace switch invalidates cache");
        Check(!intervention.State.IsActive, "Workspace block respects grace");
        clock.Advance(InterventionManager.GracePeriod);
        await agent.EvaluateFocusAsync(study);
        Check(intervention.State.IsActive, "Workspace block triggers chicken after grace");
        contexts.Value = study;
        await agent.HandleInterventionAsync(new(intervention.State.Id!.Value, InterventionAction.TemporaryAllow));
        calls = client.Calls;
        Check((await agent.EvaluateFocusAsync(study)).Source == "temporary_allow" && client.Calls == calls, "Workspace temporary allowance bypasses AI");
        var task = new TaskProfile { Id = "task_001", Name = "Development", Description = "Build WPF" };
        session.SetCurrentTask(task); rules.Load(task);
        client.Decision = "allow";
        result = await agent.EvaluateFocusAsync(Snapshot());
        Check(result.AiContextSource == "task" && client.Last.Context.CurrentTask == "Development: Build WPF", "Task takes priority over workspace");
        calls = client.Calls;
        agent.EndTask();
        Check(session.CurrentTask is null && timer.GetCurrentSession().Status == FocusSessionStatus.Stopped, "End Task preserves lifecycle semantics");
        agent.StartFocus(TimeSpan.FromMinutes(25));
        result = await agent.EvaluateFocusAsync(Snapshot());
        Check(client.Calls == calls + 1 && result.AiContextSource == "workspace" && session.CurrentTask is null, "Restart after End Task automatically uses workspace AI");
        Check(TaskAnalysisMapper.BuildWorkContext(null, new() { Id = "minimal", Name = "Coding" }) == "Workspace: Coding.", "Name-only workspace mapping");
        Check(TaskAnalysisMapper.BuildWorkContext(null, null) is null, "No context mapping");
        var empty = new WorkspaceProfile { Id = "empty", Name = " ", Description = " " };
        Check(TaskAnalysisMapper.BuildWorkContext(null, empty) is null, "Empty context mapping");
        var emptyManager = new EmptyWorkspace(empty);
        var emptyAgent = new DesktopAgentService(contexts, emptyManager, focus, timer, session, rules, intervention, client);
        calls = client.Calls;
        result = await emptyAgent.EvaluateFocusAsync(Snapshot("Postman") with { WorkspaceId = "empty" });
        Check(client.Calls == calls && result is { IsEvaluated: true, IsFocused: false, AiContextSource: "none" }, "Meaningless workspace uses deterministic fallback");
        result = await emptyAgent.EvaluateFocusAsync(Snapshot() with { WorkspaceId = null });
        Check(client.Calls == calls && !result.IsEvaluated && result.AiContextSource == "none", "No workspace skips AI");
        var transport = JsonSerializer.Serialize(TaskAnalysisMapper.Map(null, study, workspaces.GetActiveWorkspace()!));
        Check(!transport.Contains("AiContextSource"), "Context source not sent to Python");
        Console.WriteLine($"Passed {count} Workspace AI context checks.");
    }
    private sealed class EmptyWorkspace(WorkspaceProfile profile) : IWorkspaceManager
    {
        public IReadOnlyList<WorkspaceProfile> GetWorkspaces() => [profile];
        public WorkspaceProfile? GetActiveWorkspace() => profile;
        public void SetActiveWorkspace(string id) { }
        public void AddWorkspace(WorkspaceProfile workspace) => throw new NotSupportedException();
    }

    private sealed class FakeAi : ITaskAnalysisClient
    {
        public int Calls; public string Decision = "allow"; public bool Fail;
        public TaskAnalyzeRequest? Last; public Task<TaskDecision>? Pending;
        public Task<TaskDecision> AnalyzeAsync(TaskAnalyzeRequest request, CancellationToken cancellationToken = default)
        {
            Calls++; Last = request;
            if (Fail) throw new HttpRequestException("Unavailable");
            return Pending ?? Task.FromResult(new TaskDecision { Decision = Decision, Relevance = 0.5, Reason = "Test decision" });
        }
    }
    private sealed class FakeContext : IContextManager
    {
        public DesktopContext Value = new();
        public Task<DesktopContext> GetCurrentContextAsync() => Task.FromResult(Value);
    }
    private sealed class FakeDesktop : IDesktopManager
    {
        public Task<bool> ReturnToWorkAsync(DesktopContext context) => Task.FromResult(true);
        public Task OpenApplicationAsync(string path) => throw new Exception("Unexpected desktop action");
        public Task OpenUrlAsync(string url) => throw new Exception("Unexpected desktop action");
    }
    private sealed class FakeFactory(StubHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://ai.test/") };
    }
    private sealed class StubHandler : HttpMessageHandler
    {
        public string BodyResponse = "{\"decision\":\"allow\",\"relevance\":0.9,\"reason\":\"Relevant\"}";
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string? Body; public string? Path; public HttpMethod? Method;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath; Method = request.Method;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(Status) { Content = new StringContent(BodyResponse, System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
