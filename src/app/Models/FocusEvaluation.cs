namespace CluckIn.App.Models;

public sealed record FocusEvaluation
{
    // False means neutral/unknown; IsFocused should only be interpreted when evaluated.
    public bool IsEvaluated { get; init; }
    public bool IsFocused { get; init; }
    public required string Reason { get; init; }
    public string? DetectedDistraction { get; init; }
}
