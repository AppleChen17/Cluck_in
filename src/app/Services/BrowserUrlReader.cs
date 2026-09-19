using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using CluckIn.App.Interfaces;
using CluckIn.App.Models;

namespace CluckIn.App.Services;

// Read-only browser chrome inspection. Never focuses the address bar or sends keys.
public sealed class BrowserUrlReader : IBrowserUrlReader
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string?> ReadUrlAsync(ActiveWindowInfo window)
    {
        if (!OperatingSystem.IsWindows() || window.WindowHandle == 0 || !await _gate.WaitAsync(0)) return null;
        // Keep a timed-out provider call single-flight; don't accumulate blocked workers.
        var read = Task.Run(() =>
        {
            try { return Read(window); }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or
                COMException or UnauthorizedAccessException or ArgumentException) { return null; }
            finally { _gate.Release(); }
        });
        try { return await read.WaitAsync(TimeSpan.FromMilliseconds(800)); }
        catch (TimeoutException) { return null; }
    }

    private static string? Read(ActiveWindowInfo window)
    {
        if (!IsCurrent(window)) return null;
        var root = AutomationElement.FromHandle(new IntPtr(window.WindowHandle));
        var pending = new Queue<(AutomationElement Element, int Depth)>();
        pending.Enqueue((root, 0));
        var walker = TreeWalker.ControlViewWalker;
        var visited = 0;
        // Avoid inspecting web document contents, which may contain lookalike inputs.
        while (pending.Count > 0 && visited++ < 256)
        {
            var (element, depth) = pending.Dequeue();
            var info = element.Current;
            if (info.ControlType == ControlType.Document || info.IsOffscreen) continue;
            if (info.ControlType == ControlType.Edit && IsAddressBar(info.AutomationId, info.Name))
            {
                // Text being edited is not necessarily the page the user is viewing.
                if (info.HasKeyboardFocus || !element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) return null;
                var url = NormalizeUrl(((ValuePattern)value).Current.Value);
                return IsCurrent(window) ? url : null;
            }
            if (depth >= 10) continue;
            var child = walker.GetFirstChild(element);
            while (child is not null && pending.Count + visited < 256)
            {
                pending.Enqueue((child, depth + 1));
                child = walker.GetNextSibling(child);
            }
        }
        return null;
    }

    private static bool IsAddressBar(string id, string name) =>
        id is "urlbar-input" or "addressEditBox" ||
        new[] { "Address and search bar", "Search or enter address", "Search with Google or enter address",
            "網址和搜尋列", "網址與搜尋列", "位址與搜尋列", "位址和搜尋列", "地址和搜索栏",
            "搜尋或輸入網址", "搜索或输入地址", "使用 Google 搜尋或輸入網址" }
            .Any(label => name.Equals(label, StringComparison.OrdinalIgnoreCase));

    public static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Any(char.IsWhiteSpace)) return null;
        if (!value.Contains("://")) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            uri.UserInfo.Length > 0 || string.IsNullOrWhiteSpace(uri.Host)) return null;
        // Reject search phrases and browser-internal pages; accept scheme-less domains.
        if (!uri.Host.Contains('.') && uri.Host != "localhost" && uri.HostNameType != UriHostNameType.IPv6) return null;
        return uri.AbsoluteUri;
    }

    private static bool IsCurrent(ActiveWindowInfo expected)
    {
        var handle = new IntPtr(expected.WindowHandle);
        if (GetForegroundWindow() != handle) return false;
        GetWindowThreadProcessId(handle, out var processId);
        var title = new StringBuilder(32768);
        GetWindowText(handle, title, title.Capacity);
        return processId == expected.ProcessId && title.ToString() == expected.WindowTitle;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
}
