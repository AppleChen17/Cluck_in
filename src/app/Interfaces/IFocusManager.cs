using CluckIn.App.Models;

namespace CluckIn.App.Interfaces;

public interface IFocusManager
{
    void StartFocus(TimeSpan duration);
    void StopFocus();
    FocusEvaluation Evaluate(DesktopContext context, WorkspaceProfile workspace);
}
