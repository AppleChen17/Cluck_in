namespace Loupedeck.CluckInPlugin;

using System;

public class TimerHourCommand : PluginDynamicCommand{
    public TimerHourCommand()
        : base(
            displayName: "Timer Hour",
            description: "Select and display focus timer hours",
            groupName: "CluckIn")
    {
        MainController.FocusTimerChanged += this.OnStateChanged;
    }

    protected override void RunCommand(String actionParameter){
        MainController.HandleKeyEvent(7);
    }

    protected override String GetCommandDisplayName(
        String actionParameter,
        PluginImageSize imageSize)
    {
        var label =
            MainController.CurrentFocusTimerField == FocusTimerField.Hours
                ? "HR"
                : "hr";

        return $"{MainController.SelectedFocusHours:00}" +
               $"{Environment.NewLine}{label}";
    }

    private void OnStateChanged(){
        this.ActionImageChanged();
    }
}
