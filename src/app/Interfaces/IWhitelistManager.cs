using CluckIn.App.Models;

namespace CluckIn.App.Interfaces;

public interface IWhitelistManager
{
    WorkspaceProfile? CurrentWhitelist { get; }
    FocusEvaluation Evaluate(DesktopContext context, WorkspaceProfile workspace);
    void Load(TaskProfile profile);
}
