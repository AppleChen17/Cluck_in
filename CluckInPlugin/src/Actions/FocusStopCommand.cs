namespace Loupedeck.CluckInPlugin;

using System;

public class FocusStopCommand : PluginDynamicCommand{
    public FocusStopCommand()
        : base(
            displayName: "Focus Stop",
            description: "Stop the current focus timer",
            groupName: "CluckIn")
    {
        MainController.FocusTimerChanged += this.OnStateChanged;
    }

    protected override void RunCommand(String actionParameter){
        MainController.HandleKeyEvent(6);
    }

    protected override String GetCommandDisplayName(
        String actionParameter,
        PluginImageSize imageSize)
    {
        return MainController.CurrentFocusTimerState switch{
            FocusTimerControlState.Running => "STOP",
            FocusTimerControlState.Paused => "STOP",
            _ => "END"
        };
    }

    private void OnStateChanged(){
        this.ActionImageChanged();
    }
}
