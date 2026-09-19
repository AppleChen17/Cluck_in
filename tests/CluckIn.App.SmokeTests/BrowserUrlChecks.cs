using CluckIn.App.Interfaces;
using CluckIn.App.Managers;
using CluckIn.App.Models;
using CluckIn.App.Services;

static class BrowserUrlChecks
{
    public static async Task RunAsync()
    {
        var count = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException(label);
            count++;
        }
        Check(BrowserUrlReader.NormalizeUrl("youtube.com/watch?v=123") == "https://youtube.com/watch?v=123", "Scheme-less address recognized");
        Check(BrowserUrlReader.NormalizeUrl("http://localhost:5173") == "http://localhost:5173/", "Local development URL preserved");
        foreach (var value in new[] { "search query", "youtube", "chrome://newtab", "about:blank", "javascript:alert(1)", "https://github.com@youtube.com" })
            Check(BrowserUrlReader.NormalizeUrl(value) is null, "Reject search text/internal/userinfo URL");
        var reader = new FakeReader();
        var browser = new BrowserManager(reader);
        var windows = new BrowserWindow();
        var workspaces = new WorkspaceManager();
        workspaces.SetActiveWorkspace("coding");
        var timer = new TimerManager();
        timer.Start(TimeSpan.FromMinutes(25));
        var contextManager = new ContextManager(windows, browser, workspaces, timer);
        var clock = new ManualClock();
        var intervention = new InterventionManager(new NoDesktopActions(), clock);
        var whitelist = new WhitelistManager();
        var coding = workspaces.GetActiveWorkspace()!;
        FocusEvaluation Chrome(string? url, string title, WorkspaceProfile? profile = null) => whitelist.Evaluate(new()
        {
            ActiveWindow = new() { ProcessName = "CHROME.EXE", WindowTitle = title },
            Browser = new() { BrowserName = "Chrome", Url = url }
        }, profile ?? coding);
        Check(Chrome("https://chatgpt.com/c/test", "YouTube Netflix Instagram").IsFocused, "Chrome ignores blocked title on allowed URL");
        Check(!Chrome("https://youtube.com", "GitHub Documentation").IsFocused, "Allowed title cannot bypass Chrome URL");
        Check(!Chrome("https://youtube.com", "Anything", coding with { AllowedApplications = ["chrome"] }).IsFocused, "Allowed app cannot bypass Chrome URL");
        Check(Chrome("https://github.com", "Anything", coding with { BlockedApplications = ["chrome"] }).IsFocused, "Chrome app deny does not override URL-only rule");
        Check(!Chrome(null, "GitHub").IsEvaluated, "Missing Chrome URL does not fall back to title");
        Check(!Chrome("chrome://newtab", "GitHub").IsEvaluated, "Internal URL is unknown");
        Check(Chrome("https://github.com", "GitHub", coding with { AllowedDomains = [] }) is { IsEvaluated: true, IsFocused: false }, "Empty domain whitelist denies known Chrome URLs");
        Check(!Chrome("https://github.com.evil.test", "GitHub").IsFocused, "Domain suffix spoof rejected");
        Check(Chrome("https://docs.github.com", "Anything").IsFocused, "Allowed subdomain matches");
        whitelist.Load(new() { Id = "task", Name = "Task", AllowedApps = ["chrome.exe"], AllowedDomains = ["github.com", "chatgpt.com"] });
        var focus = new FocusManager(timer, whitelist, intervention);
        var context = await contextManager.GetCurrentContextAsync();
        Check(context.Browser?.Url == "https://www.youtube.com/watch?v=123", "URL flows through ContextManager");
        var evaluation = focus.Evaluate(context, whitelist.CurrentWhitelist!);
        Check(evaluation is { IsEvaluated: true, IsFocused: false }, "Allowed Chrome does not bypass YouTube domain denial");
        focus.UpdateIntervention(context, evaluation);
        clock.Advance(InterventionManager.GracePeriod);
        focus.UpdateIntervention(context, evaluation);
        Check(intervention.State is { IsActive: true, CurrentDomain: "www.youtube.com" }, "YouTube triggers chicken after grace");
        reader.Url = "https://chatgpt.com/";
        context = await contextManager.GetCurrentContextAsync();
        evaluation = focus.Evaluate(context, whitelist.CurrentWhitelist!);
        focus.UpdateIntervention(context, evaluation);
        Check(evaluation.IsFocused && !intervention.State.IsActive, "Allowed ChatGPT dismisses chicken");
        reader.Url = null;
        context = await contextManager.GetCurrentContextAsync();
        Check(!focus.Evaluate(context, whitelist.CurrentWhitelist!).IsEvaluated, "Unavailable URL stays unknown");
        var calls = reader.Calls;
        Check(await browser.GetBrowserContextAsync(new() { ProcessName = "notepad" }) is null && reader.Calls == calls, "Non-browser skips URL reader");
        Check(await new BrowserUrlReader().ReadUrlAsync(new()) is null, "Missing handle safely returns unknown");
        Console.WriteLine($"Passed {count} Browser URL checks.");
    }

    private sealed class FakeReader : IBrowserUrlReader
    {
        public string? Url { get; set; } = "https://www.youtube.com/watch?v=123";
        public int Calls { get; private set; }
        public Task<string?> ReadUrlAsync(ActiveWindowInfo window) { Calls++; return Task.FromResult(Url); }
    }
    private sealed class BrowserWindow : IWindowManager
    {
        public Task<ActiveWindowInfo> GetActiveWindowAsync() => Task.FromResult(new ActiveWindowInfo
        { ProcessName = "chrome", ProcessId = 1234, WindowTitle = "Unrelated title - Google Chrome" });
    }
    private sealed class NoDesktopActions : IDesktopManager
    {
        public Task<bool> ReturnToWorkAsync(DesktopContext context) => throw new Exception("No user action");
        public Task OpenApplicationAsync(string path) => throw new Exception("No user action");
        public Task OpenUrlAsync(string url) => throw new Exception("No user action");
    }
}
