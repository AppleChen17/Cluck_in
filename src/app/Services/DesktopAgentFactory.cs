using CluckIn.App.Managers;
using CluckIn.App.Models;

namespace CluckIn.App.Services;

/// <summary>Default in-memory composition and sample profiles for the desktop app.</summary>
public static class DesktopAgentFactory
{
    public static DesktopAgentService Create()
    {
        var workspaces = new WorkspaceManager();
        workspaces.AddWorkspace(new WorkspaceProfile
        {
            Id = "reading", Name = "Reading",
            AllowedApplications = ["AcroRd32", "Acrobat"],
            AllowedWindowKeywords = ["Documentation", "Wikipedia", ".pdf"],
            BlockedWindowKeywords = ["YouTube", "Instagram", "Netflix"]
        });
        workspaces.AddWorkspace(new WorkspaceProfile
        {
            Id = "writing", Name = "Writing",
            AllowedApplications = ["WINWORD", "notepad"],
            AllowedWindowKeywords = ["Google Docs"],
            BlockedWindowKeywords = ["YouTube", "Instagram", "Netflix"]
        });
        workspaces.AddWorkspace(new WorkspaceProfile
        {
            Id = "meeting", Name = "Meeting",
            AllowedApplications = ["ms-teams", "Teams", "Zoom"],
            AllowedWindowKeywords = ["Google Meet", "Microsoft Teams"],
            BlockedWindowKeywords = ["Instagram", "Netflix"]
        });
        var timer = new TimerManager();
        var context = new ContextManager(new WindowManager(), new BrowserManager(), workspaces, timer);
        return new DesktopAgentService(context, workspaces, new FocusManager(), timer);
    }
}
