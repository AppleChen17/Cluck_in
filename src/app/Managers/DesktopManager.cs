using System.Runtime.InteropServices;
using System.Text;
using CluckIn.App.Models;
using System.Diagnostics;
using System.IO;
using CluckIn.App.Interfaces;
using Microsoft.Extensions.Logging;

namespace CluckIn.App.Managers;

public sealed class DesktopManager(ILogger<DesktopManager> logger) : IDesktopManager
{
    public async Task<bool> ReturnToWorkAsync(DesktopContext context)
    {
        if (context.Browser is not null)
        {
            // A browser HWND does not identify a tab. Open the known allowed URL,
            // never bring a possibly changed, distracting tab to the foreground.
            if (!Uri.TryCreate(context.Browser.Url, UriKind.Absolute, out var url) ||
                (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)) return false;
            await OpenUrlAsync(url.AbsoluteUri);
            return true;
        }
        var window = new IntPtr(context.ActiveWindow.WindowHandle);
        if (window == IntPtr.Zero || !IsWindow(window)) return false;
        GetWindowThreadProcessId(window, out var processId);
        if (processId != context.ActiveWindow.ProcessId) return false;
        using var process = Process.GetProcessById((int)processId);
        if (!process.ProcessName.Equals(context.ActiveWindow.ProcessName, StringComparison.OrdinalIgnoreCase)) return false;
        var title = new StringBuilder(32768);
        GetWindowText(window, title, title.Capacity);
        // Keyword-based workspace rules may no longer apply to a changed window.
        if (title.ToString() != context.ActiveWindow.WindowTitle) return false;
        if (IsIconic(window)) ShowWindowAsync(window, 9);
        return SetForegroundWindow(window);
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

    public Task OpenApplicationAsync(string executablePath)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
            var path = Environment.ExpandEnvironmentVariables(executablePath);
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
                throw new FileNotFoundException("Application requires an existing absolute executable path.", path);
            if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Application path must point to an .exe file.");
            path = Path.GetFullPath(path);
            // Explorer also hosts the desktop shell, so a running process does not
            // mean a File Explorer window is open. Launch it to request a window.
            var isWindowsExplorer = string.Equals(path,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                StringComparison.OrdinalIgnoreCase);
            var processes = isWindowsExplorer
                ? Array.Empty<Process>()
                : Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path));
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        if (string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase))
                        {
                            logger.LogInformation("Application already running: {Path}", path);
                            return Task.CompletedTask;
                        }
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        logger.LogDebug(ex, "Cannot inspect process {ProcessId}", process.Id);
                    }
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
            using var started = Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            logger.LogInformation("Opened application {Path}", path);
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to open application {Path}", executablePath);
            throw;
        }
    }

    public Task OpenUrlAsync(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                string.IsNullOrWhiteSpace(uri.Host))
                throw new ArgumentException("URL must be an absolute HTTP or HTTPS URL.", nameof(url));
            using var started = Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
            logger.LogInformation("Opened URL {Url}", uri.AbsoluteUri);
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to open URL {Url}", url);
            throw;
        }
    }
}
