namespace CluckIn.App.Models;

public sealed record ActiveWindowInfo
{
    public long WindowHandle { get; init; }
    public string ProcessName { get; init; } = "";
    public int ProcessId { get; init; }
    public string WindowTitle { get; init; } = "";
}
