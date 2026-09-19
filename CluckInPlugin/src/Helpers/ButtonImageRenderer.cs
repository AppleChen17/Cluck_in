namespace Loupedeck.CluckInPlugin;

using System;

internal static class ButtonImageRenderer{
    public static BitmapImage DrawTimerField(
        String unit,
        Int32 value,
        Boolean selected,
        Boolean flash,
        Boolean timerActive,
        Double segmentRemainingRatio,
        PluginImageSize imageSize)
    {
        var dark = new BitmapColor(24, 24, 26);

        using(var bitmapBuilder = new BitmapBuilder(imageSize)){
            bitmapBuilder.Clear(dark);
            bitmapBuilder.DrawText($"{value:00}{Environment.NewLine}{unit}");
            return bitmapBuilder.ToImage();
        }
    }

    public static BitmapImage DrawDeskState(
        FocusTimerControlState state,
        Double elapsedProgress)
    {
        String resourceName;

        resourceName = state switch{
            FocusTimerControlState.Running =>
                $"Desk_work_{QuantizePercent(elapsedProgress):000}.png",

            FocusTimerControlState.Paused =>
                $"Desk_tea_{QuantizePercent(elapsedProgress):000}.png",

            _ => "Desk_idle.png"
        };

        return PluginResources.ReadImage(resourceName);
    }

    public static BitmapImage DrawCoopState(
        FocusTimerControlState state)
    {
        var timerActive = state is
            FocusTimerControlState.Running or
            FocusTimerControlState.Paused;

        var resourceName = timerActive
            ? "Coop_empty.png"
            : "Coop_home.png";

        return PluginResources.ReadImage(resourceName);
    }

    private static Int32 QuantizePercent(
        Double ratio)
    {
        var percent = (Int32)Math.Round(
            Math.Clamp(ratio, 0.0, 1.0) *
            100.0 / 5.0
        ) * 5;

        return Math.Clamp(percent, 0, 100);
    }
}
