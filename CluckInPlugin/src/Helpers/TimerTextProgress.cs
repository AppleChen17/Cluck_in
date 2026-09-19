namespace Loupedeck.CluckInPlugin;

using System;

internal static class TimerTextProgress{
    private const String CellGap = "\u00A0";

    public static String Render(Double ratio){
        var clamped = Double.IsFinite(ratio)
            ? Math.Clamp(ratio, 0.0, 1.0)
            : 0.0;

        var filled = (Int32)Math.Ceiling(clamped * 3.0);

        return filled switch{
            3 => "▁▁▁",
            2 => $"{CellGap}▁▁",
            1 => $"{CellGap}{CellGap}▁",
            _ => $"{CellGap}{CellGap}{CellGap}"
        };
    }
}
