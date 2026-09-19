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
            if (String.IsNullOrWhiteSpace(actionParameter))
                MainController.HandleKeyEvent(4);
            else
                MainController.SelectTask(actionParameter);
        }

        protected override String GetCommandDisplayName(
            String actionParameter,
            PluginImageSize imageSize)
        {
            return "Task";
        }
    }
}
