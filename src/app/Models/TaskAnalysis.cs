namespace CluckIn.App.Models;

// Transport DTOs matching ai-engine schemas.py; domain models remain unchanged.
public sealed record TaskAnalysisTarget(string Type, string Name, string? Title, string? Url, string? Identifier);
public sealed record TaskAnalysisSession(string Mode, string? CurrentTask, DateTimeOffset? FocusStartedAt,
    int FocusDurationSeconds, IReadOnlyList<string> AllowedApps);
public sealed record TaskAnalyzeRequest(TaskAnalysisTarget Target, TaskAnalysisSession Context);
public sealed record TaskDecision
{
    public required string Decision { get; init; }
    public required double Relevance { get; init; }
    public required string Reason { get; init; }
}
public sealed class AiEngineOptions
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:8000/";
    public int TimeoutSeconds { get; set; } = 35;
    public int CacheSeconds { get; set; } = 45;
    public int FailureCacheSeconds { get; set; } = 15;
}
