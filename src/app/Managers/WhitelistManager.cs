using CluckIn.App.Interfaces;
using CluckIn.App.Models;

namespace CluckIn.App.Managers;

public sealed class WhitelistManager : IWhitelistManager
{
    public WorkspaceProfile? CurrentWhitelist { get; private set; }

    public void Load(TaskProfile profile) => CurrentWhitelist = new()
    {
        Id = profile.Id, Name = profile.Name,
        AllowedApplications = Array.AsReadOnly(profile.AllowedApps.ToArray()),
        AllowedDomains = Array.AsReadOnly(profile.AllowedDomains.ToArray())
    };
    public FocusEvaluation Evaluate(DesktopContext context, WorkspaceProfile workspace)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(workspace);
        var window = context.ActiveWindow;
        var process = NormalizeApplication(window.ProcessName);

        if (string.IsNullOrWhiteSpace(process))
            return new() { Reason = "Active application unavailable." };

        // Chrome is website activity: never fall back to app or caption rules.
        if (process == "chrome") return EvaluateDomain(context.Browser?.Url, workspace);

        var match = workspace.BlockedApplications.FirstOrDefault(a =>
            !string.IsNullOrWhiteSpace(a) && NormalizeApplication(a) == process);
        if (match is not null)
            return Result(false, $"Application is blocked: {match}", match);

        match = MatchKeyword(workspace.BlockedWindowKeywords, window.WindowTitle);
        if (match is not null)
            return Result(false, $"Window matches blocked keyword: {match}", match);

        if (context.Browser is not null && workspace.AllowedDomains.Count > 0 && workspace.AllowedApplications.Count > 0 &&
            !workspace.AllowedApplications.Any(a => NormalizeApplication(a) == process))
            return Result(false, $"Application is not allowed: {process}", process);

        if (context.Browser is not null && workspace.AllowedDomains.Count > 0)
            return EvaluateDomain(context.Browser.Url, workspace);

        match = workspace.AllowedApplications.FirstOrDefault(a =>
            !string.IsNullOrWhiteSpace(a) && NormalizeApplication(a) == process);
        if (match is not null)
            return Result(true, $"Application is allowed: {match}");

        match = MatchKeyword(workspace.AllowedWindowKeywords, window.WindowTitle);
        if (match is not null)
            return Result(true, $"Window matches allowed keyword: {match}");

        return Result(false, "Current activity is not on the workspace whitelist.", process);
    }

    private static FocusEvaluation EvaluateDomain(string? url, WorkspaceProfile workspace)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var page) ||
            (page.Scheme != Uri.UriSchemeHttp && page.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(page.Host))
            return new() { Reason = "Browser URL unavailable; domain whitelist cannot be evaluated." };
        var host = page.IdnHost.TrimEnd('.');
        var domain = workspace.AllowedDomains.FirstOrDefault(d =>
            !string.IsNullOrWhiteSpace(d) &&
            (host.Equals(d.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase) ||
             host.EndsWith("." + d.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase)));
        return domain is not null
            ? Result(true, $"Domain is allowed: {domain}")
            : Result(false, $"Domain is not allowed: {host}", host);
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
