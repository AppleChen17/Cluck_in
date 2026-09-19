namespace Loupedeck.CluckInPlugin;

using System;

public class TimerMinuteCommand : PluginDynamicCommand{
    public TimerMinuteCommand()
        : base(
            displayName: "Timer Minute",
            description: "Select and display focus timer min",
            groupName: "CluckIn")
    {
        MainController.FocusTimerChanged +=
            this.OnStateChanged;
    }

    protected override void RunCommand(
        String actionParameter)
    {
        MainController.HandleKeyEvent(8);
    }

    protected override String GetCommandDisplayName(
        String actionParameter,
        PluginImageSize imageSize)
    {
        var state =
            MainController.CurrentFocusTimerState;

        if(state is
            FocusTimerControlState.Running or
            FocusTimerControlState.Paused)
        {
            var progress = TimerTextProgress.Render(
                MainController.GetFocusTimerSegmentRemainingRatio(
                    1
                )
            );

            return $"{MainController.DisplayFocusMinutes:00}" +
                   $"{Environment.NewLine}{progress}";
        }

        var label =
            MainController.CurrentFocusTimerField ==
                FocusTimerField.Minutes
                ? "MIN"
                : "min";

        return $"{MainController.DisplayFocusMinutes:00}" +
               $"{Environment.NewLine}{label}";
    }

    private void OnStateChanged(){
        this.ActionImageChanged();
    }
}
