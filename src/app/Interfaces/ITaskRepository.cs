using CluckIn.App.Models;

namespace CluckIn.App.Interfaces;

public interface ITaskRepository
{
    Task<TaskProfile?> GetTaskAsync(string taskId);
    Task<IReadOnlyList<TaskProfile>> GetTasksAsync();
    Task SaveTaskAsync(TaskProfile profile);
}
