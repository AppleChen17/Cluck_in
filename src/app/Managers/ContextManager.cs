using CluckIn.App.Interfaces;
using CluckIn.App.Models;

namespace CluckIn.App.Managers;

public sealed class ContextManager(
    IWindowManager windowManager,
    IBrowserManager browserManager,
    IWorkspaceManager workspaceManager,
    ITimerManager timerManager,
    ISessionManager? sessionManager = null) : IContextManager
{
    public async Task<DesktopContext> GetCurrentContextAsync()
    {
        // Only foreground-window sampling leaves the application's dispatcher.
        var window = await Task.Run(windowManager.GetActiveWindowAsync);
        var workspace = workspaceManager.GetActiveWorkspace();
        var session = timerManager.GetCurrentSession();
        return new()
        {
            ActiveWindow = window,
            Browser = browserManager.GetBrowserContext(window),
            WorkspaceId = sessionManager?.CurrentTask?.Id ?? workspace?.Id,
            WorkspaceName = sessionManager?.CurrentTask?.Name ?? workspace?.Name,
            FocusSession = session,
            FocusModeEnabled = session.Status is FocusSessionStatus.Running or FocusSessionStatus.Paused,
            TimerRunning = session.Status == FocusSessionStatus.Running
        };
    }
}
