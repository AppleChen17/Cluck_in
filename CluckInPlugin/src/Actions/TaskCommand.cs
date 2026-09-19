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
        }

        protected override void RunCommand(String actionParameter)
        {
            MainController.HandleKeyEvent(4);
        }

        protected override String GetCommandDisplayName(
            String actionParameter,
            PluginImageSize imageSize)
        {
            return "Task";
        }
    }
}