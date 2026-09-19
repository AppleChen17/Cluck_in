using CluckIn.App.Interfaces;
using CluckIn.App.Managers;
using CluckIn.App.Models;

static class InterventionChecks
{
    public static async Task RunAsync()
    {
        var count = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException(label);
            count++;
        }
        var clock = new ManualClock();
        var desktop = new RecordingDesktop();
        var intervention = new InterventionManager(desktop, clock);
        var whitelist = new WhitelistManager();
        var timer = new TimerManager(clock);
        var focus = new FocusManager(timer, whitelist, intervention);
        var rules = new WorkspaceProfile { Id = "test", Name = "Test", AllowedApplications = ["code", "chrome"], AllowedDomains = ["github.com"] };
        var work = new DesktopContext
        {
            WorkspaceId = "test", FocusModeEnabled = true, TimerRunning = true,
            FocusSession = new() { StartTime = clock.GetUtcNow(), Status = FocusSessionStatus.Running },
            ActiveWindow = new() { ProcessName = "code", ProcessId = 100, WindowHandle = 42 }
        };
        var distraction = work with { ActiveWindow = new() { ProcessName = "game", ProcessId = 200 } };
        void Observe(DesktopContext context) => focus.UpdateIntervention(context, focus.Evaluate(context, rules));
        void Trigger(DesktopContext context)
        {
            Observe(context);
            clock.Advance(InterventionManager.GracePeriod);
            Observe(context);
            Check(intervention.State.IsActive, "Grace period triggers intervention");
        }
        Observe(work);
        Check(!intervention.State.IsActive, "Allowed context does nothing");
        Observe(distraction);
        clock.Advance(TimeSpan.FromSeconds(6));
        Observe(distraction);
        Check(!intervention.State.IsActive, "No intervention before grace period");
        Observe(work);
        Observe(distraction);
        clock.Advance(TimeSpan.FromSeconds(1));
        Observe(distraction);
        Check(!intervention.State.IsActive, "Returning to work resets grace");
        clock.Advance(TimeSpan.FromSeconds(6));
        Observe(distraction);
        var snapshot = intervention.State;
        Check(snapshot is { IsActive: true, CanReturnToWork: true, Severity: InterventionSeverity.Nudge }, "Prompt retains return target");
        Observe(distraction);
        Check(intervention.State.Id == snapshot.Id, "Repeated poll does not retrigger");
        Observe(work with { ActiveWindow = new() { ProcessId = Environment.ProcessId, ProcessName = "CluckIn.App" } });
        Check(intervention.State.Id == snapshot.Id, "Clicking chicken preserves captured target");
        await intervention.HandleActionAsync(new(snapshot.Id!.Value, InterventionAction.TemporaryAllow));
        Check(!intervention.State.IsActive && snapshot.IsActive, "Temporary allow dismisses, snapshots immutable");
        clock.Advance(TimeSpan.FromMinutes(4));
        Observe(distraction);
        Check(!intervention.State.IsActive, "Allowance suppresses same app");
        Trigger(distraction with { ActiveWindow = new() { ProcessName = "other-game" } });
        Check(intervention.State.CurrentApp == "other-game", "Allowance does not allow other apps");
        Observe(work);
        clock.Advance(TimeSpan.FromMinutes(1));
        Trigger(distraction);
        Check(intervention.State.CurrentApp == "game", "Expired allowance needs a fresh grace period");
        desktop.Succeed = false;
        try { await intervention.HandleActionAsync(new(intervention.State.Id!.Value, InterventionAction.ReturnToWork)); throw new Exception("Failed return accepted"); }
        catch (InvalidOperationException) { count++; }
        Check(intervention.State.IsActive, "Failed return keeps actionable prompt");
        desktop.Succeed = true;
        var returnId = intervention.State.Id!.Value;
        await intervention.HandleActionAsync(new(returnId, InterventionAction.ReturnToWork));
        Check(desktop.Returned == work && !intervention.State.IsActive, "Return is delegated to DesktopManager");
        Observe(distraction);
        clock.Advance(TimeSpan.FromSeconds(7));
        Observe(distraction);
        Check(!intervention.State.IsActive, "Cooldown prevents immediate repeated prompt");
        try { await intervention.HandleActionAsync(new(returnId, InterventionAction.TemporaryAllow)); throw new Exception("Stale action accepted"); }
        catch (InvalidOperationException) { count++; }
        clock.Advance(TimeSpan.FromSeconds(8));
        Trigger(distraction);
        Observe(distraction with { TimerRunning = false });
        Check(!intervention.State.IsActive, "Paused session suppresses prompt");
        Trigger(distraction);
        Observe(distraction with { FocusModeEnabled = false });
        Check(!intervention.State.IsActive, "Disabled or completed focus clears prompt");
        Trigger(distraction);
        Observe(distraction with { WorkspaceId = "next-task" });
        Check(!intervention.State.IsActive, "Task switch resets prompt and grace");
        Trigger(distraction);
        focus.StopFocus();
        Check(!intervention.State.IsActive, "Stop focus clears immediately");

        var browser = work with { ActiveWindow = new() { ProcessName = "chrome" }, Browser = new() { BrowserName = "Chrome", Url = "https://youtube.com/watch" } };
        Trigger(browser);
        await intervention.HandleActionAsync(new(intervention.State.Id!.Value, InterventionAction.TemporaryAllow));
        clock.Advance(InterventionManager.Cooldown);
        Trigger(browser with { Browser = new() { BrowserName = "Chrome", Url = "https://reddit.com" } });
        Check(intervention.State.CurrentDomain == "reddit.com", "Domain allowance does not allow whole browser");
        Observe(browser with { Browser = new() { BrowserName = "Chrome", Url = null } });
        Check(!intervention.State.IsActive, "Unknown URL never triggers domain violation");
        Check(whitelist.Evaluate(browser with { Browser = new() { BrowserName = "Chrome", Url = "https://github.com.evil.example" } }, rules) is { IsEvaluated: true, IsFocused: false }, "Domain spoof rejected");
        Check(whitelist.Evaluate(browser with { Browser = new() { BrowserName = "Chrome", Url = "https://docs.github.com" } }, rules).IsFocused, "Subdomain allowed");
        Console.WriteLine($"Passed {count} Chicken Intervention checks.");
    }

    private sealed class RecordingDesktop : IDesktopManager
    {
        public bool Succeed { get; set; } = true;
        public DesktopContext? Returned { get; private set; }
        public Task<bool> ReturnToWorkAsync(DesktopContext context)
        {
            Returned = context;
            return Task.FromResult(Succeed);
        }
        public Task OpenApplicationAsync(string path) => throw new Exception("Intervention must not launch an app automatically");
        public Task OpenUrlAsync(string url) => throw new Exception("Intervention must not open a URL automatically");
    }
}
