using CluckIn.App.Models;

namespace CluckIn.App.Interfaces;

public interface IDesktopManager
{
    Task<bool> ReturnToWorkAsync(DesktopContext context);
    Task OpenApplicationAsync(string executablePath);
    Task OpenUrlAsync(string url);
}
