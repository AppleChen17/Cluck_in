namespace Loupedeck.CluckInPlugin;

using System;

public class FocusStopCommand : PluginDynamicCommand{
    public FocusStopCommand()
        : base(
            displayName: "Focus Stop",
            description: "Stop the current focus timer",
            groupName: "CluckIn")
    {
        MainController.ModeChanged += () => this.ActionImageChanged();
        IdleChickenAnimation.StatisticsChanged += this.OnIdleChanged;
        FocusChickenAnimation.FrameChanged += this.OnFocusFrameChanged;
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
        MainController.HandleKeyEvent(6);
    }

    protected override String GetCommandDisplayName(
        String actionParameter,
        PluginImageSize imageSize)
    {
        if(MainController.CurrentMode == CluckInMode.Idle){
            return IdleChickenAnimation.Statistic(v => v.SuccessfulFeedCount, "FED");
        }
        return "END";
    }

    protected override BitmapImage GetCommandImage(
        String actionParameter,
        PluginImageSize imageSize)
    {
        if(MainController.CurrentMode == CluckInMode.Idle){
            return null; // Native centered statistics text, without a coop image.
        }
        return FocusChickenAnimation.Current?.DrawKey6(
            imageSize,
            MainController.CurrentFocusTimerState
        );
    }

    private void OnFocusFrameChanged(){
        if(MainController.CurrentMode == CluckInMode.Focus){
            this.ActionImageChanged();
        }
    }

    private void OnStateChanged(){
        this.ActionImageChanged();
    }
}



