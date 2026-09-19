namespace Loupedeck.CluckInPlugin
{
    using System;

    public class AutomationCommand : PluginDynamicCommand
    {
        public AutomationCommand()
            : base(
                displayName: "AI Assist",
                description: "Change AI assist policy",
                groupName: "CluckIn")
        {
            MainController.ModeChanged += this.OnStateChanged;
            MainController.AIAssistModeChanged += this.OnStateChanged;
        }

        protected override void RunCommand(String actionParameter)
        {
            MainController.HandleKeyEvent(3);
        }

        protected override String GetCommandDisplayName(
            String actionParameter,
            PluginImageSize imageSize)
        {
            if(MainController.CurrentMode == CluckInMode.Idle){
                return "Pet";
            }

            return MainController.CurrentAIAssistMode switch{
                AIAssistMode.Off => "AI OFF",
                AIAssistMode.Suggestion => "AI SUGGEST",
                AIAssistMode.On => "AI ON",
                _ => "AI"
            };
        }

        private void OnStateChanged()
        {
            this.ActionImageChanged();
        }
    }
}