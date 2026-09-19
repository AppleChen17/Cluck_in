using CluckIn.App.Interfaces;
using CluckIn.App.Models;

namespace CluckIn.App.Managers;

public sealed class FocusManager(
    ITimerManager? timerManager = null,
    IWhitelistManager? whitelistManager = null,
    IInterventionManager? interventionManager = null) : IFocusManager
{
    private readonly ITimerManager _timer = timerManager ?? new TimerManager();
    private readonly IWhitelistManager _whitelist = whitelistManager ?? new WhitelistManager();
    public void StartFocus(TimeSpan duration)
    {
        _timer.Start(duration);
        interventionManager?.Reset();
    }
    public void StopFocus()
    {
        _timer.Stop();
        interventionManager?.Reset();
    }

    public FocusEvaluation Evaluate(DesktopContext context, WorkspaceProfile workspace) =>
        _whitelist.Evaluate(context, workspace);

    public void UpdateIntervention(DesktopContext context, FocusEvaluation evaluation) =>
        interventionManager?.Observe(context, evaluation,
            context.FocusModeEnabled && context.TimerRunning && evaluation.IsEvaluated && !evaluation.IsFocused);
}
