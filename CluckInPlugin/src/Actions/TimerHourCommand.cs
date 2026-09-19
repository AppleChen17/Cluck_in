namespace Loupedeck.CluckInPlugin;

using System;

public class TimerHourCommand : PluginDynamicCommand{
    public TimerHourCommand()
        : base(
            displayName: "Timer Hour",
            description: "Select and display focus timer hr",
            groupName: "CluckIn")
    {
        MainController.ModeChanged += () => this.ActionImageChanged();
        IdleChickenAnimation.StatisticsChanged += this.OnIdleChanged;
        MainController.FocusTimerChanged +=
            this.OnStateChanged;
    }

    private void OnIdleChanged(){
        if(MainController.CurrentMode == CluckInMode.Idle){
            this.ActionImageChanged();
        }
    }

    protected override void RunCommand(
        String actionParameter)
    {
        MainController.HandleKeyEvent(7);
    }

    protected override String GetCommandDisplayName(
        String actionParameter,
        PluginImageSize imageSize)
    {
        if(MainController.CurrentMode == CluckInMode.Idle){
            return IdleChickenAnimation.FocusTime(0);
        }

        var label =
            MainController.CurrentFocusTimerField ==
                FocusTimerField.Hours
                ? "HR"
                : "hr";

        return $"{MainController.DisplayFocusHours:00}" +
               $"{Environment.NewLine}{label}";
    }

    private void OnStateChanged(){
        this.ActionImageChanged();
    }
}
