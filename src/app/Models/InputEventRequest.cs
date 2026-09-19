using System.Text.Json;

namespace CluckIn.App.Models;

public sealed record InputEventRequest
{
    public required string Type { get; init; }
    public required string Source { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required JsonElement Payload { get; init; }
    public JsonElement Metadata { get; init; }
}
