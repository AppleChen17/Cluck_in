namespace Loupedeck.CluckInPlugin
{
    using System;

    public class CounterCommand : PluginDynamicCommand
    {
        public CounterCommand()
            : base(
                displayName: "Mode",
                description: "Switch between Focus and Idle mode",
                groupName: "CluckIn")
        {
            MainController.ModeChanged += this.OnModeChanged;
        }

        protected override void RunCommand(String actionParameter)
        {
            MainController.HandleKeyEvent(1);
        }

        protected override String GetCommandDisplayName(
            String actionParameter,
            PluginImageSize imageSize)
        {
            return MainController.CurrentMode == CluckInMode.Focus
                ? "FOCUS"
                : "IDLE";
        }

        private void OnModeChanged()
        {
            this.ActionImageChanged();
        }
    }
}
