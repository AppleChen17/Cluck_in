namespace Loupedeck.CluckInPlugin
{
    using System;

    public class CounterAdjustment : PluginDynamicAdjustment
    {
        private const Int32 TicksPerStep = 8;

        private Int32 _pendingTicks = 0;

        private FocusTimerField _lastField =
            MainController.CurrentFocusTimerField;

        public CounterAdjustment()
            : base(
                displayName: "Focus Duration",
                description: "Adjust the selected timer field",
                groupName: "CluckIn",
                hasReset: true)
        {
            MainController.FocusTimerChanged +=
                this.OnTimerChanged;
        }

        protected override void ApplyAdjustment(
            String actionParameter,
            Int32 diff)
        {
            if(this._lastField !=
                MainController.CurrentFocusTimerField)
            {
                this._pendingTicks = 0;
                this._lastField =
                    MainController.CurrentFocusTimerField;
            }

            this._pendingTicks += diff;

            var steps =
                this._pendingTicks / TicksPerStep;

            if(steps == 0){
                return;
            }

            this._pendingTicks %= TicksPerStep;

            MainController.AdjustFocusDuration(steps);

            this.AdjustmentValueChanged();
        }

        protected override void RunCommand(
            String actionParameter)
        {
            MainController.ResetFocusDuration();
            this.AdjustmentValueChanged();
        }

        protected override String GetAdjustmentValue(
            String actionParameter)
        {
            return
                $"{MainController.DisplayFocusHours:00}:" +
                $"{MainController.DisplayFocusMinutes:00}:" +
                $"{MainController.DisplayFocusSeconds:00}";
        }

        private void OnTimerChanged(){
            this.AdjustmentValueChanged();
        }
    }
}



