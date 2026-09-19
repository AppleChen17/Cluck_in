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
            IdleChickenAnimation.StatisticsChanged += this.OnIdleChanged;
            MainController.ModeChanged += this.OnStateChanged;
            MainController.AIAssistModeChanged += this.OnStateChanged;
        }

        private void OnIdleChanged(){
            if(MainController.CurrentMode == CluckInMode.Idle){
                this.ActionImageChanged();
            }
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
                return IdleChickenAnimation.Statistic(v => v.FeedCount, "FEED");
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