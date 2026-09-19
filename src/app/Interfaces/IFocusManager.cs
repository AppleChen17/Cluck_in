using CluckIn.App.Models;

namespace CluckIn.App.Interfaces;

public interface IFocusManager
{
    FocusEvaluation Evaluate(DesktopContext context, WorkspaceProfile workspace);
}
