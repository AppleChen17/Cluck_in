using CluckIn.App.Interfaces;
using CluckIn.App.Models;

namespace CluckIn.App.Managers;

public sealed class SessionManager : ISessionManager
{
    public TaskProfile? CurrentTask { get; private set; }
    public void ClearCurrentTask() => CurrentTask = null;

    public void SetCurrentTask(TaskProfile profile) => CurrentTask = profile with
    {
        Apps = [.. profile.Apps], Urls = [.. profile.Urls],
        AllowedApps = [.. profile.AllowedApps], AllowedDomains = [.. profile.AllowedDomains]
    };
}
