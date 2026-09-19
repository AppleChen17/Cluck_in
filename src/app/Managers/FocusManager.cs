using CluckIn.App.Interfaces;
using CluckIn.App.Models;

namespace CluckIn.App.Managers;

public sealed class FocusManager(ITimerManager? timerManager = null) : IFocusManager
{
    private readonly ITimerManager _timer = timerManager ?? new TimerManager();
    public void StartFocus(TimeSpan duration) => _timer.Start(duration);
    public void StopFocus() => _timer.Stop();

    public FocusEvaluation Evaluate(DesktopContext context, WorkspaceProfile workspace)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(workspace);
        var window = context.ActiveWindow;
        var process = NormalizeApplication(window.ProcessName);

        var match = workspace.BlockedApplications.FirstOrDefault(a =>
            !string.IsNullOrWhiteSpace(a) && NormalizeApplication(a) == process);
        if (match is not null)
            return Result(false, $"Application is blocked: {match}", match);

        match = MatchKeyword(workspace.BlockedWindowKeywords, window.WindowTitle);
        if (match is not null)
            return Result(false, $"Window matches blocked keyword: {match}", match);

        if (context.Browser is not null && workspace.AllowedDomains.Count > 0)
        {
            if (!Uri.TryCreate(context.Browser.Url, UriKind.Absolute, out var page))
                return new() { Reason = "Browser URL unavailable; domain whitelist cannot be evaluated." };
            var domain = workspace.AllowedDomains.FirstOrDefault(d =>
                page.Host.Equals(d, StringComparison.OrdinalIgnoreCase) ||
                page.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
            return domain is not null
                ? Result(true, $"Domain is allowed: {domain}")
                : Result(false, $"Domain is not allowed: {page.Host}", page.Host);
        }

        match = workspace.AllowedApplications.FirstOrDefault(a =>
            !string.IsNullOrWhiteSpace(a) && NormalizeApplication(a) == process);
        if (match is not null)
            return Result(true, $"Application is allowed: {match}");

        match = MatchKeyword(workspace.AllowedWindowKeywords, window.WindowTitle);
        if (match is not null)
            return Result(true, $"Window matches allowed keyword: {match}");

        return new() { Reason = "No workspace rule matched the current activity." };
    }

    private static string NormalizeApplication(string value)
    {
        value = value.Trim();
        return (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value)
            .ToLowerInvariant();
    }

    private static string? MatchKeyword(IEnumerable<string> keywords, string title) =>
        keywords.FirstOrDefault(k => !string.IsNullOrWhiteSpace(k) &&
            title.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static FocusEvaluation Result(bool focused, string reason, string? distraction = null) =>
        new() { IsEvaluated = true, IsFocused = focused, Reason = reason, DetectedDistraction = distraction };
}
