namespace CluckIn.App.Models;

public sealed record ExternalMessage
{
    public string Id { get; init; } = "";
    public string Source { get; init; } = "";
    public string Sender { get; init; } = "";
    public string? Title { get; init; }
    public string Content { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; }
    public bool Unread { get; init; } = true;
}

public sealed record UrgentMessage(ExternalMessage Message, string Reason);

public sealed class ExternalMessagesOptions
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:8100/";
}
