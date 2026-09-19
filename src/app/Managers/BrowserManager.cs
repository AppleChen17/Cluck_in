using CluckIn.App.Interfaces;
using CluckIn.App.Models;

namespace CluckIn.App.Managers;

public sealed class BrowserManager : IBrowserManager
{
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

        // TODO: A future Chrome Extension adapter may provide the real active-tab URL.
        // This implementation only infers context from the foreground process and caption.
        return new() { BrowserName = name, PageTitle = title, Url = null };
    }
}
