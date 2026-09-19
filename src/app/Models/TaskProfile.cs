namespace CluckIn.App.Models;

public sealed record TaskProfile
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public List<string> Apps { get; init; } = [];
    public List<string> Urls { get; init; } = [];
    public List<string> AllowedApps { get; init; } = [];
    public List<string> AllowedDomains { get; init; } = [];
    public int? FocusDurationMinutes { get; init; }
}
