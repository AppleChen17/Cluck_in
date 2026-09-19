using Microsoft.Extensions.Options;
using CluckIn.App.Managers;
using CluckIn.App.Models;
using CluckIn.App.Interfaces;
using CluckIn.App.Repositories;
using Microsoft.Extensions.DependencyInjection;
using System.IO;

namespace CluckIn.App.Services;

/// <summary>Default in-memory composition and sample profiles for the desktop app.</summary>
public static class DesktopAgentFactory
{
    public static DesktopAgentService Create()
    {
        var workspaces = CreateWorkspaces();
        var timer = new TimerManager();
        var context = new ContextManager(new WindowManager(), new BrowserManager(), workspaces, timer);
        var whitelist = new WhitelistManager();
        var intervention = new InterventionManager(new DesktopManager(Microsoft.Extensions.Logging.Abstractions.NullLogger<DesktopManager>.Instance));
        return new DesktopAgentService(context, workspaces, new FocusManager(timer, whitelist, intervention), timer,
            whitelistManager: whitelist, interventionManager: intervention);
    }

    public static void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<IWorkspaceManager>(_ => CreateWorkspaces());
        services.AddSingleton<ChickenProgressReporter>();
        services.AddHostedService(provider => provider.GetRequiredService<ChickenProgressReporter>());
        services.AddSingleton<ITimerManager>(provider => new ProgressTrackingTimer(
            new TimerManager(), provider.GetRequiredService<ChickenProgressReporter>().Record));
        services.AddSingleton<IWindowManager, WindowManager>();
        services.AddSingleton<IBrowserManager, BrowserManager>();
        services.AddSingleton<IBrowserUrlReader, BrowserUrlReader>();
        services.AddSingleton<IContextManager, ContextManager>();
        services.AddSingleton<IFocusManager, FocusManager>();
        services.AddSingleton<IInterventionManager, InterventionManager>();
        services.AddSingleton<ISessionManager, SessionManager>();
        services.AddSingleton<IWhitelistManager, WhitelistManager>();
        services.AddSingleton<IDesktopManager, DesktopManager>();
        services.AddSingleton<ITaskManager, TaskManager>();
        services.AddOptions<AiEngineOptions>().Validate(o =>
            Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out var uri) &&
            (uri.Scheme == "http" || uri.Scheme == "https") && o.TimeoutSeconds > 0 &&
            o.CacheSeconds > 0 && o.FailureCacheSeconds > 0, "Invalid AiEngine settings.");
        services.AddHttpClient("TaskAnalysis", (provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<AiEngineOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        }).RemoveAllLoggers();
        services.AddSingleton<ITaskAnalysisClient, TaskAnalysisClient>();
        services.AddOptions<ExternalMessagesOptions>();
        services.AddHttpClient("ExternalMessages", (provider, client) =>
        {
            client.BaseAddress = new Uri(provider.GetRequiredService<IOptions<ExternalMessagesOptions>>().Value.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(5);
        }).RemoveAllLoggers();
        services.AddSingleton<UrgentMessageService>();
        services.AddSingleton<DesktopAgentService>();
        services.AddSingleton<ITaskRepository>(_ =>
        {
            var repository = new InMemoryTaskRepository();
            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("CLUCK_IN_CODE_PATH"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe")
            };
            repository.SaveTaskAsync(new()
            {
                Id = "task_001", Name = "Cluck In Development",
                Description = "Develop and test Cluck In.",
                Apps =
                [
                    candidates.FirstOrDefault(p => p is not null && File.Exists(p)) ?? candidates[1]!,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")
                ],
                Urls = ["http://localhost:5173", "https://github.com/AppleChen17/Cluck_in", "https://chatgpt.com"],
                AllowedApps = ["Code.exe", "explorer.exe", "chrome.exe"],
                AllowedDomains = ["localhost", "github.com", "chatgpt.com"],
                FocusDurationMinutes = 50
            }).GetAwaiter().GetResult();
            return repository;
        });
    }

    private static WorkspaceManager CreateWorkspaces()
    {
        var workspaces = new WorkspaceManager();
        workspaces.AddWorkspace(new WorkspaceProfile
        {
            Id = "reading", Name = "Reading",
            AllowedApplications = ["AcroRd32", "Acrobat"],
            AllowedDomains = ["wikipedia.org", "learn.microsoft.com", "developer.mozilla.org"],
            AllowedWindowKeywords = ["Documentation", "Wikipedia", ".pdf"],
            BlockedWindowKeywords = ["YouTube", "Instagram", "Netflix"]
        });
        workspaces.AddWorkspace(new WorkspaceProfile
        {
            Id = "writing", Name = "Writing",
            AllowedApplications = ["WINWORD", "notepad"],
            AllowedDomains = ["docs.google.com"],
            AllowedWindowKeywords = ["Google Docs"],
            BlockedWindowKeywords = ["YouTube", "Instagram", "Netflix"]
        });
        workspaces.AddWorkspace(new WorkspaceProfile
        {
            Id = "meeting", Name = "Meeting",
            AllowedApplications = ["ms-teams", "Teams", "Zoom"],
            AllowedDomains = ["meet.google.com", "teams.microsoft.com", "zoom.us"],
            AllowedWindowKeywords = ["Google Meet", "Microsoft Teams"],
            BlockedWindowKeywords = ["Instagram", "Netflix"]
        });
        return workspaces;
    }
}
