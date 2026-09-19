namespace Loupedeck.CluckInPlugin;

using System;
using System.Collections.Generic;

public sealed class InputEventRequest{
    public string Type { get; init; } = "";
    public string Source { get; init; } = "logitech";
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public Dictionary<string, object> Payload { get; init; } =
        new Dictionary<string, object>();

    public Dictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();
}