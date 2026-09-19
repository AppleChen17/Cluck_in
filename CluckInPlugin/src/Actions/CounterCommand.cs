namespace Loupedeck.CluckInPlugin
{
    using System;

    public class CounterCommand : PluginDynamicCommand
    {
        private Int32 _counter = 0;

        public CounterCommand()
            : base(
                displayName: "Key 1",
                description: "CluckIn Key 1",
                groupName: "CluckIn")
        {
        }

        protected override void RunCommand(String actionParameter)
        {
            this._counter++;
            MainController.HandleKeyEvent(1);

            this.ActionImageChanged();
            PluginLog.Info($"Counter value is {this._counter}");
        }

        protected override String GetCommandDisplayName(
            String actionParameter,
            PluginImageSize imageSize) =>
            $"Key 1{Environment.NewLine}{this._counter}";
    }
}