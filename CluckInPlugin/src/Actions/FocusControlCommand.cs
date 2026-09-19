namespace Loupedeck.CluckInPlugin;

using System;

public class FocusControlCommand : PluginDynamicCommand{
    public FocusControlCommand()
        : base(
            displayName: "Focus Start",
            description: "Start, pause, or resume focus timer",
            groupName: "CluckIn")
    {
        MainController.ModeChanged += this.OnStateChanged;
        IdleChickenAnimation.FrameChanged += this.OnIdleFrameChanged;
        MainController.FocusTimerChanged +=
            this.OnStateChanged;
    }

    protected override void RunCommand(
        String actionParameter)
    {
        MainController.HandleKeyEvent(5);
    }

    protected override String GetCommandDisplayName(
        String actionParameter,
        PluginImageSize imageSize)
    {
        if(MainController.CurrentMode != CluckInMode.Focus){
            return "\u200B";
        }

        return MainController.CurrentFocusTimerState switch{
            FocusTimerControlState.Ready => "START",
            FocusTimerControlState.Running => "PAUSE",
            FocusTimerControlState.Paused => "RESUME",
            FocusTimerControlState.Completed => "START",
            _ => "START"
        };
    }

    protected override BitmapImage GetCommandImage(
        String actionParameter,
        PluginImageSize imageSize)
    {
        if(MainController.CurrentMode == CluckInMode.Idle){
            return IdleChickenAnimation.Current?.Draw(imageSize);
        }

        return ButtonImageRenderer.DrawDeskState(
            MainController.CurrentFocusTimerState,
            MainController.FocusProgress
        );
    }

    private void OnIdleFrameChanged(){
        if(MainController.CurrentMode == CluckInMode.Idle){
            this.ActionImageChanged();
        }
    }

    private void OnStateChanged(){
        this.ActionImageChanged();
    }
}
