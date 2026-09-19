using CluckIn.App.Models;

namespace CluckIn.App.Interfaces;

public interface ITaskManager
{
    Task<TaskProfile?> GetTaskAsync(string taskId);
    Task StartTaskAsync(string taskId);
}
