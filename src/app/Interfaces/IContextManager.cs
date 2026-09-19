using CluckIn.App.Models;

namespace CluckIn.App.Interfaces;

public interface IContextManager
{
    Task<DesktopContext> GetCurrentContextAsync();
}
