using CluckIn.App.Interfaces;
using CluckIn.App.Models;

namespace CluckIn.App.Managers;

public sealed class WhitelistManager : IWhitelistManager
{
    public WorkspaceProfile? CurrentWhitelist { get; private set; }

    public void Load(TaskProfile profile) => CurrentWhitelist = new()
    {
        Id = profile.Id, Name = profile.Name,
        AllowedApplications = Array.AsReadOnly(profile.AllowedApps.ToArray()),
        AllowedDomains = Array.AsReadOnly(profile.AllowedDomains.ToArray())
    };
}
