using CluckIn.App.Interfaces;
using CluckIn.App.Models;

namespace CluckIn.App.Managers;

public sealed record FocusProgress(string SessionId, long Seconds, bool Completed);

// Observes the authoritative timer; never changes its results or transition rules.
public sealed class ProgressTrackingTimer(ITimerManager timer, Action<FocusProgress> report) : ITimerManager
{
    private string? _sessionId;
    private FocusProgress? _last;

    private FocusSession Capture()
    {
        var session = timer.GetCurrentSession();
        if (_sessionId is not null && session.Status is FocusSessionStatus.Running or
            FocusSessionStatus.Paused or FocusSessionStatus.Completed)
        {
            var progress = new FocusProgress(_sessionId,
                Math.Max(0, (long)(session.Duration - session.RemainingTime).TotalSeconds),
                session.Status == FocusSessionStatus.Completed);
            if (progress != _last)
            {
                report(progress);
                _last = progress;
            }
        }
        return session;
    }

    public void Start(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) { timer.Start(duration); return; }
        Capture();
        timer.Start(duration);
        _sessionId = Guid.NewGuid().ToString("N");
        _last = null;
        Capture();
    }
    public void Pause() { timer.Pause(); Capture(); }
    public void Resume() { timer.Resume(); Capture(); }
    public void Stop() { Capture(); timer.Stop(); }
    public FocusSession GetCurrentSession() => Capture();
}
