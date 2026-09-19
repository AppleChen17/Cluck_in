namespace CluckIn.App.Models;

public enum InterventionSeverity { Peek, Nudge, Block }
public enum InterventionAction { None, ShowIntervention, ReturnToWork, TemporaryAllow }

// Immutable snapshots can be shared safely by the API and both views.
public sealed record InterventionState
{
    public Guid? Id { get; init; }
    public bool IsActive { get; init; }
    public InterventionSeverity Severity { get; init; } = InterventionSeverity.Peek;
    public string Reason { get; init; } = string.Empty;
    public string? CurrentApp { get; init; }
    public string? CurrentDomain { get; init; }
    public DateTimeOffset? TriggeredAt { get; init; }
    public InterventionAction Action { get; init; }
    public bool CanReturnToWork { get; init; }
}

public sealed record InterventionActionRequest(Guid InterventionId, InterventionAction Action);
