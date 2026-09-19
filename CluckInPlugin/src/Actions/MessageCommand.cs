namespace Loupedeck.CluckInPlugin
{
    using System;

    public class MessageCommand : PluginDynamicCommand
    {
        public MessageCommand()
            : base(
                displayName: "Messages / Pet",
                description: "Context-sensitive message or pet action",
                groupName: "CluckIn")
        {
            MainController.ModeChanged += this.OnModeChanged;
        }

        protected override void RunCommand(String actionParameter)
        {
            MainController.HandleKeyEvent(2);
        }

        protected override String GetCommandDisplayName(
            String actionParameter,
            PluginImageSize imageSize)
        {
            return MainController.CurrentMode == CluckInMode.Focus
                ? "Messages"
                : "PET";
        }

        private void OnModeChanged()
        {
            this.ActionImageChanged();
        }
    }
}