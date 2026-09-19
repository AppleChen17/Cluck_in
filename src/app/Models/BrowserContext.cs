namespace CluckIn.App.Models;

public sealed record BrowserContext
{
    public required string BrowserName { get; init; }
    public string PageTitle { get; init; } = "";
    public string? Url { get; init; }
}
