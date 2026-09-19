using CluckIn.App.Interfaces;
using CluckIn.App.Managers;
using CluckIn.App.Models;
using CluckIn.App.Services;

var assertions = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    assertions++;
}
void Reject(Action action, string name)
{
    try { action(); }
    catch (ArgumentException) { assertions++; return; }
    throw new InvalidOperationException(name);
}

var workspaces = new WorkspaceManager();
Check(workspaces.GetActiveWorkspace() is null, "Initially no active workspace");
workspaces.SetActiveWorkspace("CODING");
var coding = workspaces.GetActiveWorkspace()!;
Reject(() => workspaces.SetActiveWorkspace("missing"), "Unknown workspace rejected");
Reject(() => workspaces.AddWorkspace(coding), "Duplicate workspace rejected");
var rules = new List<string> { "reader" };
workspaces.AddWorkspace(new() { Id = "reading", Name = "Reading", AllowedApplications = rules });
rules.Clear();
Check(workspaces.GetWorkspaces()[1].AllowedApplications.Count == 1, "Rules copied on add");

var focus = new FocusManager();
FocusEvaluation Evaluate(string process, string title, WorkspaceProfile? workspace = null) =>
    focus.Evaluate(new() { ActiveWindow = new() { ProcessName = process, WindowTitle = title } }, workspace ?? coding);
Check(Evaluate("Code.EXE", "project").IsFocused, "VS Code focused, normalized process");
Check(Evaluate("WindowsTerminal", "terminal").IsFocused, "Terminal focused");
Check(Evaluate("chrome", "github - Google Chrome").IsFocused, "GitHub focused, case insensitive");
var youtube = Evaluate("chrome", "YouTube - Google Chrome");
Check(youtube.IsEvaluated && !youtube.IsFocused && youtube.DetectedDistraction == "YouTube", "YouTube distracted");
Check(!Evaluate("code", "YouTube GitHub").IsFocused, "Blocked keyword beats allowed app and keyword");
Check(!Evaluate("chrome", "GitHub", coding with { BlockedApplications = ["CHROME.exe"] }).IsFocused, "Blocked app takes priority");
Check(!Evaluate("chrome", "Unclassified page").IsEvaluated, "Unknown page neutral");
Check(!Evaluate("", "").IsEvaluated, "Missing window neutral");

var browsers = new BrowserManager();
foreach (var (process, title, name) in new[]
{
    ("CHROME.exe", "GitHub - Google Chrome", "Chrome"),
    ("msedge", "GitHub - Microsoft Edge", "Edge"),
    ("firefox", "GitHub — Mozilla Firefox", "Firefox")
})
{
    var browser = browsers.GetBrowserContext(new() { ProcessName = process, WindowTitle = title });
    Check(browser is { PageTitle: "GitHub", Url: null } && browser.BrowserName == name, name);
}
Check(browsers.GetBrowserContext(new() { ProcessName = "code", WindowTitle = "Google Chrome" }) is null, "Title alone cannot identify browser");

var clock = new ManualClock();
var timer = new TimerManager(clock);
Check(timer.GetCurrentSession().Status == FocusSessionStatus.Idle, "Timer idle");
Reject(() => timer.Start(TimeSpan.Zero), "Zero duration rejected");
timer.Start(TimeSpan.FromMinutes(10));
var original = timer.GetCurrentSession();
clock.Advance(TimeSpan.FromMinutes(2));
timer.Pause();
clock.Advance(TimeSpan.FromHours(1));
Check(timer.GetCurrentSession().RemainingTime == TimeSpan.FromMinutes(8), "Pause freezes time");
timer.Resume();
clock.Advance(TimeSpan.FromMinutes(3));
Check(timer.GetCurrentSession().RemainingTime == TimeSpan.FromMinutes(5), "Resume preserves elapsed time");
Check(timer.GetCurrentSession().StartTime == original.StartTime, "Resume preserves start time");
Check(original.RemainingTime == TimeSpan.FromMinutes(10), "Snapshots immutable");
clock.Advance(TimeSpan.FromMinutes(6));
Check(timer.GetCurrentSession() is { Status: FocusSessionStatus.Completed, RemainingTime.Ticks: 0 }, "Expiry clamps remaining time");
timer.Resume();
Check(timer.GetCurrentSession().Status == FocusSessionStatus.Completed, "Completed cannot resume");

var windows = new FakeWindowManager();
var context = new ContextManager(windows, browsers, workspaces, timer);
var agent = new DesktopAgentService(context, workspaces, focus, timer);
Check(!(await agent.EvaluateFocusAsync()).IsEvaluated, "Inactive focus neutral");
agent.StartFocus("coding", TimeSpan.FromMinutes(25));
Check((await agent.GetContextAsync()) is { WorkspaceId: "coding", FocusModeEnabled: true, TimerRunning: true, Browser.BrowserName: "Chrome" }, "Context aggregation");
Check((await agent.EvaluateFocusAsync()).IsFocused, "Service evaluation");
agent.PauseFocus();
Check((await agent.GetContextAsync()) is { FocusModeEnabled: true, TimerRunning: false }, "Paused focus stays enabled");
agent.ResumeFocus();
clock.Advance(TimeSpan.FromMinutes(25));
Check(!(await agent.GetContextAsync()).FocusModeEnabled, "Expiry disables focus mode");
agent.StartFocus("coding", TimeSpan.FromMinutes(1));
Reject(() => agent.StartFocus("reading", TimeSpan.FromMinutes(-1)), "Invalid start rejected");
Check(agent.GetActiveWorkspace()?.Id == "coding", "Invalid duration does not change workspace");
agent.StopFocus();
Check(!(await agent.GetContextAsync()).FocusModeEnabled && timer.GetCurrentSession().Status == FocusSessionStatus.Stopped, "Stop disables mode");
Console.WriteLine($"Passed {assertions} Desktop Agent checks.");
await ViewModelChecks.RunAsync();
await TaskChecks.RunAsync();
if (args.Contains("--ui")) await WpfLaunchChecks.RunAsync();

sealed class FakeWindowManager : IWindowManager
{
    public Task<ActiveWindowInfo> GetActiveWindowAsync() => Task.FromResult(new ActiveWindowInfo
    {
        ProcessName = "chrome", ProcessId = 1234, WindowTitle = "GitHub - Cluck In - Google Chrome"
    });
}

sealed class ManualClock : TimeProvider
{
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
    public void Advance(TimeSpan duration) => _ticks += duration.Ticks;
}
