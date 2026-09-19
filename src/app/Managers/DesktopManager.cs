using System.Diagnostics;
using System.IO;
using CluckIn.App.Interfaces;
using Microsoft.Extensions.Logging;

namespace CluckIn.App.Managers;

public sealed class DesktopManager(ILogger<DesktopManager> logger) : IDesktopManager
{
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
            var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path));
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
