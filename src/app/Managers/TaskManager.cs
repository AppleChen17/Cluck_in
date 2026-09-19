using CluckIn.App.Interfaces;
using CluckIn.App.Models;
using Microsoft.Extensions.Logging;

namespace CluckIn.App.Managers;

public sealed class TaskManager(
    IDesktopManager desktopManager,
    ISessionManager sessionManager,
    IWhitelistManager whitelistManager,
    IFocusManager focusManager,
    ITaskRepository repository,
    ILogger<TaskManager> logger) : ITaskManager
{
    private readonly SemaphoreSlim _startLock = new(1, 1);

    public Task<TaskProfile?> GetTaskAsync(string taskId) => repository.GetTaskAsync(taskId);

    public async Task StartTaskAsync(string taskId)
    {
        await _startLock.WaitAsync();
        try
        {
            var profile = await GetTaskAsync(taskId)
                ?? throw new KeyNotFoundException($"Task not found: {taskId}");
            if (profile.FocusDurationMinutes is <= 0)
                throw new ArgumentException("Focus duration must be positive.");

            sessionManager.SetCurrentTask(profile);
            whitelistManager.Load(profile);
            // Switching tasks must not inherit the previous task's timer.
            focusManager.StopFocus();
            foreach (var app in profile.Apps.Distinct(StringComparer.OrdinalIgnoreCase))
                await TryLaunchAsync(app, desktopManager.OpenApplicationAsync);
            foreach (var url in profile.Urls.Distinct(StringComparer.Ordinal))
                await TryLaunchAsync(url, desktopManager.OpenUrlAsync);
            if (profile.FocusDurationMinutes is int minutes)
                focusManager.StartFocus(TimeSpan.FromMinutes(minutes));
            logger.LogInformation("Started task {TaskId} ({TaskName})", profile.Id, profile.Name);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to start task {TaskId}", taskId);
            throw;
        }
        finally { _startLock.Release(); }
    }

    private async Task TryLaunchAsync(string target, Func<string, Task> launch)
    {
        try { await launch(target); }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not open {Target}; continuing task startup", target);
        }
    }
}
