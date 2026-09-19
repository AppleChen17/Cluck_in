using System.Collections.Concurrent;
using CluckIn.App.Interfaces;
using CluckIn.App.Models;

namespace CluckIn.App.Repositories;

public sealed class InMemoryTaskRepository : ITaskRepository
{
    private readonly ConcurrentDictionary<string, TaskProfile> _tasks = new(StringComparer.Ordinal);

    public Task<TaskProfile?> GetTaskAsync(string taskId) =>
        Task.FromResult(_tasks.TryGetValue(taskId, out var profile) ? Copy(profile) : null);

    public Task<IReadOnlyList<TaskProfile>> GetTasksAsync() =>
        Task.FromResult<IReadOnlyList<TaskProfile>>(_tasks.Values.OrderBy(t => t.Id).Select(Copy).ToArray());

    public Task SaveTaskAsync(TaskProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Name);
        if (profile.FocusDurationMinutes is <= 0)
            throw new ArgumentException("Focus duration must be positive.");
        if (profile.Apps is null || profile.Urls is null || profile.AllowedApps is null || profile.AllowedDomains is null ||
            profile.Apps.Concat(profile.Urls).Concat(profile.AllowedApps).Concat(profile.AllowedDomains).Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Task lists must contain non-empty strings and cannot be null.");
        if (profile.AllowedDomains.Any(d => Uri.CheckHostName(d) == UriHostNameType.Unknown))
            throw new ArgumentException("Allowed domains must be hostnames, without schemes, ports or paths.");
        _tasks[profile.Id] = Copy(profile);
        return Task.CompletedTask;
    }

    private static TaskProfile Copy(TaskProfile profile) => profile with
    {
        Apps = [.. profile.Apps], Urls = [.. profile.Urls],
        AllowedApps = [.. profile.AllowedApps], AllowedDomains = [.. profile.AllowedDomains]
    };
}
