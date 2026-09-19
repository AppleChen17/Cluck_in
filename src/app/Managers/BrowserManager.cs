using CluckIn.App.Interfaces;
using CluckIn.App.Models;
using CluckIn.App.Services;

namespace CluckIn.App.Managers;

public sealed class BrowserManager(IBrowserUrlReader? urlReader = null) : IBrowserManager
{
    private readonly IBrowserUrlReader _urlReader = urlReader ?? new BrowserUrlReader();

    public async Task<BrowserContext?> GetBrowserContextAsync(ActiveWindowInfo activeWindow)
    {
        var browser = GetBrowserContext(activeWindow);
        return browser is null ? null : browser with { Url = await _urlReader.ReadUrlAsync(activeWindow) };
    }

    public BrowserContext? GetBrowserContext(ActiveWindowInfo activeWindow)
    {
        ArgumentNullException.ThrowIfNull(activeWindow);
        var process = activeWindow.ProcessName.Trim();
        if (process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            process = process[..^4];

        var (name, suffix) = process.ToLowerInvariant() switch
        {
            "chrome" => ("Chrome", "Google Chrome"),
            "msedge" => ("Edge", "Microsoft Edge"),
            "firefox" => ("Firefox", "Mozilla Firefox"),
            _ => ("", "")
        };
        if (name.Length == 0)
            return null;

        var title = activeWindow.WindowTitle.Trim();
        foreach (var separator in new[] { " - ", " — ", " – " })
        {
            var ending = separator + suffix;
            if (title.EndsWith(ending, StringComparison.OrdinalIgnoreCase))
            {
                title = title[..^ending.Length].TrimEnd();
                break;
            }
        }

        // Synchronous callers receive caption-only context; the desktop uses the async URL reader.
        return new() { BrowserName = name, PageTitle = title, Url = null };
    }
}
