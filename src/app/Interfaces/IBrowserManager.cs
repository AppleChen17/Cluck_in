using CluckIn.App.Models;

namespace CluckIn.App.Interfaces;

public interface IBrowserManager
{
    BrowserContext? GetBrowserContext(ActiveWindowInfo activeWindow);
    Task<BrowserContext?> GetBrowserContextAsync(ActiveWindowInfo activeWindow) =>
        Task.FromResult(GetBrowserContext(activeWindow));
}
