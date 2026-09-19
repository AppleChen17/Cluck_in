namespace Loupedeck.CluckInPlugin;

using System;

public class FocusControlCommand : PluginDynamicCommand{
    public FocusControlCommand()
        : base(
            displayName: "Focus Control",
            description: "Start, pause, or resume focus timer",
            groupName: "CluckIn")
    {
        MainController.ModeChanged += this.OnStateChanged;
        MainController.FocusTimerChanged += this.OnStateChanged;
    }

    protected override void RunCommand(String actionParameter){
        MainController.HandleKeyEvent(5);
    }

    protected override String GetCommandDisplayName(
        String actionParameter,
        PluginImageSize imageSize)
    {
        if(MainController.CurrentMode != CluckInMode.Focus){
            return "FOCUS ONLY";
        }

        return MainController.CurrentFocusTimerState switch{
            FocusTimerControlState.Ready => "START",
            FocusTimerControlState.Running => "PAUSE",
            FocusTimerControlState.Paused => "RESUME",
            FocusTimerControlState.Completed => "START",
            _ => "FOCUS"
        };
    }

    private void OnStateChanged(){
        this.ActionImageChanged();
    }
}
