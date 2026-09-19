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
        var highlight = new BitmapColor(31, 116, 145);
        var running = new BitmapColor(37, 128, 94);
        var completion = new BitmapColor(220, 157, 36);

        // The SDK maps None to 0x0 and ToImage() then returns null.
        if(imageSize == PluginImageSize.None){
            imageSize = PluginImageSize.Width90;
        }

        using(var bitmapBuilder = new BitmapBuilder(imageSize)){
            bitmapBuilder.Clear(
                flash
                    ? completion
                    : selected && !timerActive
                        ? highlight
                        : dark
            );

            if(timerActive && !flash){
                var ratio = Double.IsFinite(
                    segmentRemainingRatio
                )
                    ? Math.Clamp(
                        segmentRemainingRatio,
                        0.0,
                        1.0
                    )
                    : 0.0;

                var fillWidth = (Int32)Math.Round(
                    bitmapBuilder.Width * ratio
                );

                if(fillWidth > 0){
                    bitmapBuilder.FillRectangle(
                        bitmapBuilder.Width - fillWidth,
                        0,
                        fillWidth,
                        bitmapBuilder.Height,
                        running
                    );
                }
            }

            var displayUnit =
                selected && !timerActive
                    ? unit.ToUpperInvariant()
                    : unit;

            // Keep the SDK's default font size, with two centered, non-overlapping rows.
            var rowHeight = BitmapBuilder.GetDefaultFontSize(imageSize) + 4;
            var textTop = (bitmapBuilder.Height - (2 * rowHeight)) / 2;
            var textColor = new BitmapColor(255, 255, 255);

            bitmapBuilder.DrawText(
                $"{value:00}",
                0, textTop, bitmapBuilder.Width, rowHeight,
                color: textColor
            );
            bitmapBuilder.DrawText(
                displayUnit,
                0, textTop + rowHeight, bitmapBuilder.Width, rowHeight,
                color: textColor
            );

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
