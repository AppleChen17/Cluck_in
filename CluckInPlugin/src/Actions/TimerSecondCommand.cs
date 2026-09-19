namespace Loupedeck.CluckInPlugin;

using System;

public class TimerSecondCommand : PluginDynamicCommand{
    public TimerSecondCommand()
        : base(
            displayName: "Timer Second",
            description: "Select and display focus timer sec",
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
        MainController.HandleKeyEvent(9);
    }

    protected override String GetCommandDisplayName(
        String actionParameter,
        PluginImageSize imageSize)
    {
        if(MainController.CurrentMode == CluckInMode.Idle){
            return IdleChickenAnimation.FocusTime(2);
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
