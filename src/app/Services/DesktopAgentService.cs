using CluckIn.App.Interfaces;
using CluckIn.App.Models;

namespace CluckIn.App.Services;

// Share the same workspace/timer instances with ContextManager.
// This lightweight in-memory module expects calls from a single application loop.
public sealed class DesktopAgentService(
    IContextManager contextManager,
    IWorkspaceManager workspaceManager,
    IFocusManager focusManager,
    ITimerManager timerManager,
    ISessionManager? sessionManager = null,
    IWhitelistManager? whitelistManager = null,
    IInterventionManager? interventionManager = null)
{
    public Task<DesktopContext> GetContextAsync() => contextManager.GetCurrentContextAsync();

    public async Task<FocusEvaluation> EvaluateFocusAsync(DesktopContext? context = null)
    {
        // A caller may evaluate its displayed snapshot without sampling another window.
        context ??= await GetContextAsync();
        if (!context.FocusModeEnabled)
        {
            var inactive = new FocusEvaluation { Reason = "Focus Mode is disabled." };
            focusManager.UpdateIntervention(context, inactive);
            return inactive;
        }

        var workspace = sessionManager?.CurrentTask?.Id == context.WorkspaceId && sessionManager?.CurrentTask is not null
            ? whitelistManager?.CurrentWhitelist
            : workspaceManager.GetWorkspaces().FirstOrDefault(w => w.Id == context.WorkspaceId);
        var evaluation = workspace is null
            ? new() { Reason = "No active workspace." }
            : focusManager.Evaluate(context, workspace);
        focusManager.UpdateIntervention(context, evaluation);
        return evaluation;
    }

    public InterventionState Intervention => interventionManager?.State ?? new();
    public async Task<InterventionState> HandleInterventionAsync(InterventionActionRequest request)
    {
        var context = await GetContextAsync();
        interventionManager?.SynchronizeSession(context);
        if (interventionManager is null) throw new InvalidOperationException("Interventions are unavailable.");
        return await interventionManager.HandleActionAsync(request);
    }

    public IReadOnlyList<WorkspaceProfile> GetWorkspaces() => workspaceManager.GetWorkspaces();
    public WorkspaceProfile? GetActiveWorkspace() => workspaceManager.GetActiveWorkspace();
    public void AddWorkspace(WorkspaceProfile workspace) => workspaceManager.AddWorkspace(workspace);
    public void SetActiveWorkspace(string workspaceId)
    {
        workspaceManager.SetActiveWorkspace(workspaceId);
        sessionManager?.ClearCurrentTask();
        interventionManager?.Reset();
    }

    public void StartFocus(string workspaceId, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        workspaceManager.SetActiveWorkspace(workspaceId);
        sessionManager?.ClearCurrentTask();
        interventionManager?.Reset();
        focusManager.StartFocus(duration);
    }

    public void PauseFocus()
    {
        timerManager.Pause();
        interventionManager?.Reset();
    }
    public void ResumeFocus() => timerManager.Resume();
    public void StopFocus() => focusManager.StopFocus();
}
