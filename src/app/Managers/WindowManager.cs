using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CluckIn.App.Interfaces;
using CluckIn.App.Models;

namespace CluckIn.App.Managers;

public sealed class WindowManager : IWindowManager
{
    public Task<ActiveWindowInfo> GetActiveWindowAsync()
    {
        // These APIs are synchronous and quick; no worker thread is needed.
        return Task.FromResult(ReadActiveWindow());
    }

    private static ActiveWindowInfo ReadActiveWindow()
    {
        if (!OperatingSystem.IsWindows())
            return new();

        try
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero)
                return new();

            // Windows captions are bounded; allow titles to change between API calls.
            var title = new StringBuilder(32768);
            GetWindowText(window, title, title.Capacity);
            GetWindowThreadProcessId(window, out var processId);
            if (processId == 0 || processId > int.MaxValue)
                return new() { WindowTitle = title.ToString() };

            var info = new ActiveWindowInfo
            {
                ProcessId = (int)processId,
                WindowTitle = title.ToString()
            };
            try
            {
                using var process = Process.GetProcessById(info.ProcessId);
                return info with { ProcessName = process.ProcessName };
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // The process may exit or deny access after the foreground window is read.
                return info;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or Win32Exception)
        {
            return new();
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
