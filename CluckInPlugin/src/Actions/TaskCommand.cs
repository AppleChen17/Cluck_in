namespace Loupedeck.CluckInPlugin
{
    using System;

    public class TaskCommand : PluginDynamicCommand
    {
        public TaskCommand()
            : base(
                displayName: "Task",
                description: "Select current task",
                groupName: "CluckIn")
        {
            MainController.ModeChanged += () => this.ActionImageChanged();
            IdleChickenAnimation.StatisticsChanged += this.OnIdleChanged;
        }

        private void OnIdleChanged(){
            if(MainController.CurrentMode == CluckInMode.Idle){
                this.ActionImageChanged();
            }
        }

        protected override void RunCommand(String actionParameter)
        {
            MainController.HandleKeyEvent(4);
        }

        protected override String GetCommandDisplayName(
            String actionParameter,
            PluginImageSize imageSize)
        {
            if(MainController.CurrentMode == CluckInMode.Idle){
                return IdleChickenAnimation.Statistic(v => v.PatCount, "PATS");
            }
            return "Task";
        }
    }
}