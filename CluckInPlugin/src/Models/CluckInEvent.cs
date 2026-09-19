namespace Loupedeck.CluckInPlugin;

using System;

public sealed class CluckInEvent{
    public int KeyId { get; init; }

    public CluckInMode Mode { get; init; }

    public CluckInAction Action { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}