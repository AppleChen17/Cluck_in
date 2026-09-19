using CluckIn.App.Interfaces;
using CluckIn.App.Models;

namespace CluckIn.App.Managers;

public sealed class TimerManager(TimeProvider? timeProvider = null) : ITimerManager
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private FocusSession _session = new();
    private long _runningSince;
    private TimeSpan _remainingAtResume;

    public void Start(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");

        _session = new()
        {
            StartTime = _clock.GetUtcNow(),
            Duration = duration,
            RemainingTime = duration,
            Status = FocusSessionStatus.Running
        };
        _remainingAtResume = duration;
        _runningSince = _clock.GetTimestamp();
    }

    public void Pause()
    {
        Refresh();
        if (_session.Status == FocusSessionStatus.Running)
            _session = _session with { Status = FocusSessionStatus.Paused };
    }

    public void Resume()
    {
        if (_session.Status != FocusSessionStatus.Paused)
            return;
        _remainingAtResume = _session.RemainingTime;
        _runningSince = _clock.GetTimestamp();
        _session = _session with { Status = FocusSessionStatus.Running };
    }

    public void Stop()
    {
        Refresh();
        if (_session.Status is FocusSessionStatus.Running or FocusSessionStatus.Paused)
            _session = _session with { Status = FocusSessionStatus.Stopped, RemainingTime = TimeSpan.Zero };
    }

    public FocusSession GetCurrentSession()
    {
        Refresh();
        return _session;
    }

    private void Refresh()
    {
        if (_session.Status != FocusSessionStatus.Running)
            return;

        // Monotonic elapsed time avoids countdown jumps when the wall clock changes.
        var remaining = _remainingAtResume - _clock.GetElapsedTime(_runningSince);
        _session = _session with
        {
            RemainingTime = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero,
            Status = remaining > TimeSpan.Zero ? FocusSessionStatus.Running : FocusSessionStatus.Completed
        };
    }
}
