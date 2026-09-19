using CluckIn.App.Interfaces;
using CluckIn.App.Models;

namespace CluckIn.App.Managers;

// Called on the application dispatcher, just like the session and timer managers.
public sealed class InterventionManager(IDesktopManager desktopManager, TimeProvider? timeProvider = null) : IInterventionManager
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(7);
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan TemporaryAllowance = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, long> _allowances = new(StringComparer.OrdinalIgnoreCase);
    private string? _scope;
    private string? _target;
    private long _pendingSince;
    private long? _cooldownSince;
    private DesktopContext? _lastAllowed;
    private bool _handlingAction;
    public InterventionState State { get; private set; } = new();

    public void Reset()
    {
        State = new();
        _scope = _target = null;
        _lastAllowed = null;
        _cooldownSince = null;
        _allowances.Clear();
    }

    public void SynchronizeSession(DesktopContext context)
    {
        var scope = $"{context.WorkspaceId}|{context.FocusSession.StartTime:O}";
        if (_scope != scope || !context.FocusModeEnabled) Reset();
        _scope = scope;
        if (!context.FocusModeEnabled || !context.TimerRunning) ClearPrompt();
    }

    public void Observe(DesktopContext context, FocusEvaluation evaluation, bool shouldIntervene)
    {
        SynchronizeSession(context);
        if (!context.FocusModeEnabled || !context.TimerRunning) return;
        // Clicking the chicken must not replace the offending activity or dismiss its actions.
        if (context.ActiveWindow.ProcessId == Environment.ProcessId)
        {
            if (!State.IsActive) ClearPrompt();
            return;
        }
        if (_handlingAction) return;
        if (evaluation.IsEvaluated && evaluation.IsFocused)
        {
            if (context.Browser is null ? context.ActiveWindow.WindowHandle != 0 :
                Uri.TryCreate(context.Browser.Url, UriKind.Absolute, out var workUrl) &&
                (workUrl.Scheme == Uri.UriSchemeHttp || workUrl.Scheme == Uri.UriSchemeHttps))
                _lastAllowed = context;
            ClearPrompt();
            return;
        }
        if (!shouldIntervene)
        {
            ClearPrompt();
            return;
        }
        var domain = Uri.TryCreate(context.Browser?.Url, UriKind.Absolute, out var uri) ? uri.IdnHost.ToLowerInvariant() : null;
        var app = context.ActiveWindow.ProcessName.Trim().ToLowerInvariant();
        if (app.EndsWith(".exe")) app = app[..^4];
        var target = $"{app}|{domain}";
        foreach (var expired in _allowances.Where(p => _clock.GetElapsedTime(p.Value) >= TemporaryAllowance).Select(p => p.Key).ToArray())
            _allowances.Remove(expired);
        if (_allowances.ContainsKey(target) ||
            (_cooldownSince is long since && _clock.GetElapsedTime(since) < Cooldown))
        {
            ClearPrompt();
            return;
        }
        if (_target != target)
        {
            ClearPrompt();
            _target = target;
            _pendingSince = _clock.GetTimestamp();
        }
        if (State.IsActive || _clock.GetElapsedTime(_pendingSince) < GracePeriod) return;
        State = new()
        {
            Id = Guid.NewGuid(), IsActive = true, Severity = InterventionSeverity.Nudge,
            Reason = evaluation.Reason, CurrentApp = context.ActiveWindow.ProcessName,
            CurrentDomain = domain, TriggeredAt = _clock.GetUtcNow(),
            Action = InterventionAction.ShowIntervention, CanReturnToWork = _lastAllowed is not null
        };
    }

    public async Task<InterventionState> HandleActionAsync(InterventionActionRequest request)
    {
        if (request.Action is not (InterventionAction.ReturnToWork or InterventionAction.TemporaryAllow))
            throw new ArgumentException("Unsupported intervention action.");
        if (_handlingAction || !State.IsActive || State.Id != request.InterventionId)
            throw new InvalidOperationException("This intervention is no longer active. Refresh and try again.");
        _handlingAction = true;
        try
        {
            if (request.Action == InterventionAction.ReturnToWork)
            {
                if (_lastAllowed is null || !await desktopManager.ReturnToWorkAsync(_lastAllowed))
                    throw new InvalidOperationException("No available work window. Open an allowed app, or allow this activity temporarily.");
            }
            else if (_target is not null) _allowances[_target] = _clock.GetTimestamp();
            // A task may have changed while a desktop operation was awaiting completion.
            if (State.Id != request.InterventionId) return State;
            _cooldownSince = _clock.GetTimestamp();
            ClearPrompt();
            State = State with { Action = request.Action };
            return State;
        }
        finally { _handlingAction = false; }
    }

    private void ClearPrompt()
    {
        State = new();
        _target = null;
    }
}
