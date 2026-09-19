using CluckIn.App.Models;

namespace CluckIn.App.Services;

public static class TaskAnalysisMapper
{
    public static string? BuildWorkContext(TaskProfile? task, WorkspaceProfile? workspace)
    {
        if (task is not null)
        {
            var text = string.Join(": ", new[] { task.Name, task.Description }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        if (workspace is null) return null;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(workspace.Name)) parts.Add($"Workspace: {workspace.Name.Trim()}.");
        if (!string.IsNullOrWhiteSpace(workspace.Description)) parts.Add(workspace.Description.Trim());
        void AddRules(string label, IEnumerable<string> rules)
        {
            var entries = rules.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToArray();
            if (entries.Length > 0) parts.Add($"{label}: {string.Join(", ", entries)}.");
        }
        AddRules("Allowed applications", workspace.AllowedApplications);
        AddRules("Allowed domains", workspace.AllowedDomains);
        AddRules("Allowed window keywords", workspace.AllowedWindowKeywords);
        AddRules("Blocked applications", workspace.BlockedApplications);
        AddRules("Blocked window keywords", workspace.BlockedWindowKeywords);
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    public static TaskAnalyzeRequest Map(TaskProfile? task, DesktopContext context, WorkspaceProfile rules)
    {
        var browser = context.Browser;
        var target = browser is null
            ? new TaskAnalysisTarget("app", context.ActiveWindow.ProcessName, context.ActiveWindow.WindowTitle,
                null, context.ActiveWindow.ProcessName)
            : new TaskAnalysisTarget("web", browser.BrowserName, browser.PageTitle, browser.Url, null);
        var taskText = BuildWorkContext(task, rules);
        return new(target, new("focus", taskText, context.FocusSession.StartTime,
            (int)context.FocusSession.Duration.TotalSeconds, rules.AllowedApplications.ToArray()));
    }
}
