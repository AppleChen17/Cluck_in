using CluckIn.App.Interfaces;
using CluckIn.App.Models;

namespace CluckIn.App.Managers;

public sealed class ContextManager(
    IWindowManager windowManager,
    IBrowserManager browserManager,
    IWorkspaceManager workspaceManager,
    ITimerManager timerManager) : IContextManager
{
    public async Task<DesktopContext> GetCurrentContextAsync()
    {
        var window = await windowManager.GetActiveWindowAsync();
        var workspace = workspaceManager.GetActiveWorkspace();
        var session = timerManager.GetCurrentSession();
        return new()
        {
            ActiveWindow = window,
            Browser = browserManager.GetBrowserContext(window),
            WorkspaceId = workspace?.Id,
            WorkspaceName = workspace?.Name,
            FocusSession = session,
            FocusModeEnabled = session.Status is FocusSessionStatus.Running or FocusSessionStatus.Paused,
            TimerRunning = session.Status == FocusSessionStatus.Running
        };
    }
}
