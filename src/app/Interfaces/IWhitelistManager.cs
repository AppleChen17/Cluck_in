using CluckIn.App.Models;

namespace CluckIn.App.Interfaces;

public interface IWhitelistManager
{
    WorkspaceProfile? CurrentWhitelist { get; }
    void Load(TaskProfile profile);
}
