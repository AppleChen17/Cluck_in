using CluckIn.App.Models;

namespace CluckIn.App.Interfaces;

public interface IWorkspaceManager
{
    IReadOnlyList<WorkspaceProfile> GetWorkspaces();
    WorkspaceProfile? GetActiveWorkspace();
    void SetActiveWorkspace(string workspaceId);
    void AddWorkspace(WorkspaceProfile workspace);
}
