using CluckIn.App.Models;

namespace CluckIn.App.Interfaces;

public interface ITimerManager
{
    void Start(TimeSpan duration);
    void Pause();
    void Resume();
    void Stop();
    FocusSession GetCurrentSession();
}
