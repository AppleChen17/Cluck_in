namespace Loupedeck.CluckInPlugin;

using System;

public class TimerSecondCommand : PluginDynamicCommand{
    public TimerSecondCommand()
        : base(
            displayName: "Timer Second",
            description: "Select and display focus timer sec",
            groupName: "CluckIn")
    {
        MainController.FocusTimerChanged +=
            this.OnStateChanged;
    }

    protected override void RunCommand(
        String actionParameter)
    {
        MainController.HandleKeyEvent(9);
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
                    2
                )
            );

            return $"{MainController.DisplayFocusSeconds:00}" +
                   $"{Environment.NewLine}{progress}";
        }

        var label =
            MainController.CurrentFocusTimerField ==
                FocusTimerField.Seconds
                ? "SEC"
                : "sec";

        return $"{MainController.DisplayFocusSeconds:00}" +
               $"{Environment.NewLine}{label}";
    }

    private void OnStateChanged(){
        this.ActionImageChanged();
    }
}
