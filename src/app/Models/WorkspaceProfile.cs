namespace CluckIn.App.Models;

public sealed record WorkspaceProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<string> AllowedApplications { get; init; } = [];
    public IReadOnlyList<string> AllowedDomains { get; init; } = [];
    public IReadOnlyList<string> AllowedWindowKeywords { get; init; } = [];
    public IReadOnlyList<string> BlockedApplications { get; init; } = [];
    public IReadOnlyList<string> BlockedWindowKeywords { get; init; } = [];
}
