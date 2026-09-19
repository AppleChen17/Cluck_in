namespace CluckIn.App.Models;

public enum FocusSessionStatus { Idle, Running, Paused, Stopped, Completed }

public sealed record FocusSession
{
    public DateTimeOffset? StartTime { get; init; }
    public TimeSpan Duration { get; init; }
    public TimeSpan RemainingTime { get; init; }
    public FocusSessionStatus Status { get; init; } = FocusSessionStatus.Idle;
}
