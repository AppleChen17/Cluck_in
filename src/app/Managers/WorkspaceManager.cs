using CluckIn.App.Interfaces;
using CluckIn.App.Models;

namespace CluckIn.App.Managers;

public sealed class WorkspaceManager : IWorkspaceManager
{
    private readonly List<WorkspaceProfile> _workspaces = [];
    private string? _activeWorkspaceId;

    public WorkspaceManager()
    {
        AddWorkspace(new WorkspaceProfile
        {
            Id = "coding",
            Name = "Coding",
            Description = "Software development and technical work",
            AllowedApplications = ["code", "devenv", "WindowsTerminal"],
            AllowedDomains = ["localhost", "github.com", "stackoverflow.com", "chatgpt.com", "learn.microsoft.com", "developer.mozilla.org"],
            AllowedWindowKeywords = ["GitHub", "Stack Overflow", "Documentation", "ChatGPT"],
            BlockedWindowKeywords = ["YouTube", "Instagram", "Netflix"]
        });
    }

    public IReadOnlyList<WorkspaceProfile> GetWorkspaces() => _workspaces.ToArray();

    public WorkspaceProfile? GetActiveWorkspace() =>
        _workspaces.Find(w => string.Equals(w.Id, _activeWorkspaceId, StringComparison.OrdinalIgnoreCase));

    public void SetActiveWorkspace(string workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var workspace = _workspaces.Find(w => string.Equals(w.Id, workspaceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown workspace: {workspaceId}", nameof(workspaceId));
        _activeWorkspaceId = workspace.Id;
    }

    public void AddWorkspace(WorkspaceProfile workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace.Name);
        if (_workspaces.Any(w => string.Equals(w.Id, workspace.Id, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Workspace already exists: {workspace.Id}", nameof(workspace));

        // Own the rule collections so callers cannot mutate stored profiles accidentally.
        _workspaces.Add(workspace with
        {
            AllowedApplications = Array.AsReadOnly(workspace.AllowedApplications.ToArray()),
            AllowedDomains = Array.AsReadOnly(workspace.AllowedDomains.ToArray()),
            AllowedWindowKeywords = Array.AsReadOnly(workspace.AllowedWindowKeywords.ToArray()),
            BlockedApplications = Array.AsReadOnly(workspace.BlockedApplications.ToArray()),
            BlockedWindowKeywords = Array.AsReadOnly(workspace.BlockedWindowKeywords.ToArray())
        });
    }
}
