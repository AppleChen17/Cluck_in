using CluckIn.App.Interfaces;
using CluckIn.App.Models;

namespace CluckIn.App.Services;

// Share the same workspace/timer instances with ContextManager.
// This lightweight in-memory module expects calls from a single application loop.
public sealed class DesktopAgentService(
    IContextManager contextManager,
    IWorkspaceManager workspaceManager,
    IFocusManager focusManager,
    ITimerManager timerManager)
{
    public Task<DesktopContext> GetContextAsync() => contextManager.GetCurrentContextAsync();

    public async Task<FocusEvaluation> EvaluateFocusAsync(DesktopContext? context = null)
    {
        // A caller may evaluate its displayed snapshot without sampling another window.
        context ??= await GetContextAsync();
        if (!context.FocusModeEnabled)
            return new() { Reason = "Focus Mode is disabled." };

        var workspace = workspaceManager.GetWorkspaces().FirstOrDefault(w => w.Id == context.WorkspaceId);
        return workspace is null
            ? new() { Reason = "No active workspace." }
            : focusManager.Evaluate(context, workspace);
    }

    public IReadOnlyList<WorkspaceProfile> GetWorkspaces() => workspaceManager.GetWorkspaces();
    public WorkspaceProfile? GetActiveWorkspace() => workspaceManager.GetActiveWorkspace();
    public void AddWorkspace(WorkspaceProfile workspace) => workspaceManager.AddWorkspace(workspace);
    public void SetActiveWorkspace(string workspaceId) => workspaceManager.SetActiveWorkspace(workspaceId);

    public void StartFocus(string workspaceId, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        workspaceManager.SetActiveWorkspace(workspaceId);
        timerManager.Start(duration);
    }

    public void PauseFocus() => timerManager.Pause();
    public void ResumeFocus() => timerManager.Resume();
    public void StopFocus() => timerManager.Stop();
}
