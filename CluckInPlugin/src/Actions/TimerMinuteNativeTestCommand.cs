namespace Loupedeck.CluckInPlugin;

using System;

public class TimerMinuteNativeTestCommand : PluginDynamicCommand{
    public TimerMinuteNativeTestCommand()
        : base(
            displayName: "Timer Minute Native Test",
            description: "Fresh native-text test for focus timer minutes",
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
