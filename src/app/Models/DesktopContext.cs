namespace CluckIn.App.Models;

public sealed record DesktopContext
{
    public ActiveWindowInfo ActiveWindow { get; init; } = new();
    public BrowserContext? Browser { get; init; }
    public string? WorkspaceId { get; init; }
    public string? WorkspaceName { get; init; }
    public bool FocusModeEnabled { get; init; }
    public bool TimerRunning { get; init; }
    public FocusSession FocusSession { get; init; } = new();
}
