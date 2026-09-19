using CluckIn.App.Models;

namespace CluckIn.App.Interfaces;

public interface IInterventionManager
{
    InterventionState State { get; }
    void Observe(DesktopContext context, FocusEvaluation evaluation, bool shouldIntervene);
    void Reset();
    void SynchronizeSession(DesktopContext context);
    Task<InterventionState> HandleActionAsync(InterventionActionRequest request);
}
