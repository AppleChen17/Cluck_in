namespace Loupedeck.CluckInPlugin;

using System;

public class TimerMinuteCommand : PluginDynamicCommand{
    public TimerMinuteCommand()
        : base(
            displayName: "Timer Minute",
            description: "Select and display focus timer min",
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
        MainController.HandleKeyEvent(8);
    }

    protected override String GetCommandDisplayName(
        String actionParameter,
        PluginImageSize imageSize)
    {
        if(MainController.CurrentMode == CluckInMode.Idle){
            return IdleChickenAnimation.FocusTime(1);
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
