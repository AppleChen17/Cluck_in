using CluckIn.App.Models;

namespace CluckIn.App.Interfaces;

public interface ISessionManager
{
    TaskProfile? CurrentTask { get; }
    void SetCurrentTask(TaskProfile profile);
    void ClearCurrentTask();
}
