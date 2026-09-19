namespace CluckIn.App.Models;

public sealed record MainInputEvent(string Type, string Source, DateTimeOffset Timestamp,
    System.Text.Json.JsonElement Payload, System.Text.Json.JsonElement Metadata);

public enum AIAssistRoutingMode
{
    Off,
    Suggestion,
    On
}

public sealed record ExternalMessageContract
{
    public required string Id {get; init;}
    public required string Source {get; init;}
    public required string Sender {get; init;}
    public string? Title {get; init;}
    public required string Content {get; init;}
    public required DateTimeOffset Timestamp {get; init;}
    public required bool Unread {get; init;}
    public IReadOnlyDictionary<string, object?> Metadata {get; init;} =
        new Dictionary<string, object?>();
}

public sealed record AIDecisionContract
{
    public required string MessageId {get; init;}
    public required string Decision {get; init;}
    public required double Relevance {get; init;}
    public required double Urgency {get; init;}
    public required string Reason {get; init;}
    public IReadOnlyDictionary<string, object?> Metadata {get; init;} =
        new Dictionary<string, object?>();

    public bool RequiresReply {get; init;}
    public string? ReplyDraft {get; init;}

    public bool IsUrgent =>
        string.Equals(Decision, "urgent", StringComparison.OrdinalIgnoreCase);
}

public sealed record FocusMessageRecord
{
    public required ExternalMessageContract Message {get; init;}
    public AIDecisionContract? Decision {get; init;}
    public string? ReplySuggestion {get; init;}
    public required DateTimeOffset AnalysisTimestamp {get; init;}
}

public sealed record ExternalReplyResult
{
    public required bool Success {get; init;}
    public string? Error {get; init;}
}
