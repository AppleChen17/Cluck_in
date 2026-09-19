using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CluckIn.App.Interfaces;
using CluckIn.App.Models;

namespace CluckIn.App.Services;

// Share the same workspace/timer instances with ContextManager.
// This lightweight in-memory module expects calls from a single application loop.
public sealed class DesktopAgentService(
    IContextManager contextManager,
    IWorkspaceManager workspaceManager,
    IFocusManager focusManager,
    ITimerManager timerManager,
    ISessionManager? sessionManager = null,
    IWhitelistManager? whitelistManager = null,
    IInterventionManager? interventionManager = null,
    ITaskAnalysisClient? taskAnalysisClient = null,
    IOptions<AiEngineOptions>? aiOptions = null,
    ILogger<DesktopAgentService>? logger = null,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly AiEngineOptions _options = aiOptions?.Value ?? new();
    private string? _analysisKey;
    private Task<TaskDecision?>? _analysis;
    private CancellationTokenSource? _analysisCancellation;
    private long? _completedAt;
    private bool _reported;

    private void ClearAnalysis()
    {
        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        _analysisCancellation = null;
        _analysis = null;
        _analysisKey = null;
        _completedAt = null;
        _reported = false;
    }

    // This operation only produces a result. It never mutates focus/intervention state.
    private async Task<TaskDecision?> RequestAnalysisAsync(TaskAnalyzeRequest request, CancellationToken cancellation)
    {
        try { return await taskAnalysisClient!.AnalyzeAsync(request, cancellation).WaitAsync(cancellation).ConfigureAwait(false); }
        catch (Exception ex)
        {
            // Do not log request URLs, page titles, task descriptions or response bodies.
            logger?.LogWarning("[AI] Analysis unavailable ({ErrorType}); using deterministic fallback.", ex.GetType().Name);
            return null;
        }
    }

    private FocusEvaluation Analyze(DesktopContext context, WorkspaceProfile rules, TaskProfile? task, FocusEvaluation fallback)
    {
        var request = TaskAnalysisMapper.Map(task, context, rules);
        var contextSource = task is null ? "workspace" : "task";
        var key = JsonSerializer.Serialize(new { ContextSource = contextSource, TaskId = task?.Id, Request = request, context.ActiveWindow.ProcessId,
            context.ActiveWindow.WindowHandle, Rules = rules });
        if (_analysisKey != key)
        {
            ClearAnalysis();
            _analysisKey = key;
            // A new page/title must receive its own grace period, even for immediate results.
            focusManager.UpdateIntervention(context, new() { Source = "ai_pending", Reason = "Context changed." });
            logger?.LogInformation("[Focus] AI context source: {ContextSource}", contextSource);
            logger?.LogInformation("[Focus] Context changed; analyzing {TargetType} outside whitelist.", request.Target.Type);
        }
        if (_analysis is { IsCompletedSuccessfully: true })
        {
            _completedAt ??= _clock.GetTimestamp();
            var ttl = _analysis.Result is null ? _options.FailureCacheSeconds : _options.CacheSeconds;
            if (_clock.GetElapsedTime(_completedAt.Value) >= TimeSpan.FromSeconds(ttl))
            {
                ClearAnalysis();
                _analysisKey = key;
            }
        }
        if (_analysis is null)
        {
            _analysisCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            _analysis = RequestAnalysisAsync(request, _analysisCancellation.Token);
        }
        if (!_analysis.IsCompletedSuccessfully)
            return new() { Source = "ai_pending", AiContextSource = contextSource, Reason = "Checking current activity with AI…" };
        _completedAt ??= _clock.GetTimestamp();
        var result = _analysis.Result;
        if (result is null) return fallback with { Source = "fallback", AiContextSource = contextSource };
        if (!_reported)
        {
            logger?.LogInformation("[AI] Task analysis decision: {Decision}", result.Decision);
            if (result.Decision == "block") logger?.LogInformation("[Focus] Starting intervention grace period.");
            _reported = true;
        }
        return new() { IsEvaluated = true, IsFocused = result.Decision is "allow" or "warn", Source = "ai", AiContextSource = contextSource, Reason = result.Reason };
    }

    public Task<DesktopContext> GetContextAsync() => contextManager.GetCurrentContextAsync();

    public async Task<FocusEvaluation> EvaluateFocusAsync(DesktopContext? context = null)
    {
        // A caller may evaluate its displayed snapshot without sampling another window.
        context ??= await GetContextAsync();
        if (!context.FocusModeEnabled)
        {
            ClearAnalysis();
            var inactive = new FocusEvaluation { Reason = "Focus Mode is disabled." };
            focusManager.UpdateIntervention(context, inactive);
            return inactive;
        }

        var task = sessionManager?.CurrentTask;
        var workspace = sessionManager?.CurrentTask?.Id == context.WorkspaceId && sessionManager?.CurrentTask is not null
            ? whitelistManager?.CurrentWhitelist
            : workspaceManager.GetWorkspaces().FirstOrDefault(w => w.Id == context.WorkspaceId);
        var evaluation = workspace is null
            ? new() { Reason = "No active workspace." }
            : focusManager.Evaluate(context, workspace);
        interventionManager?.SynchronizeSession(context);
        if (context.TimerRunning && interventionManager?.IsTemporarilyAllowed(context) == true)
        {
            ClearAnalysis();
            evaluation = new() { IsEvaluated = true, IsFocused = true, Source = "temporary_allow", Reason = "Activity temporarily allowed." };
        }
        else if (context.TimerRunning && context.ActiveWindow.ProcessId != Environment.ProcessId &&
            !string.IsNullOrWhiteSpace(context.ActiveWindow.ProcessName) &&
            !(evaluation.IsEvaluated && evaluation.IsFocused) && workspace is not null &&
            (task is null || task.Id == context.WorkspaceId) &&
            TaskAnalysisMapper.BuildWorkContext(task, workspace) is not null && taskAnalysisClient is not null)
            evaluation = Analyze(context, workspace, task, evaluation);
        else ClearAnalysis();
        focusManager.UpdateIntervention(context, evaluation);
        return evaluation;
    }

    public InterventionState Intervention => interventionManager?.State ?? new();
    public async Task<InterventionState> HandleInterventionAsync(InterventionActionRequest request)
    {
        var context = await GetContextAsync();
        interventionManager?.SynchronizeSession(context);
        if (interventionManager is null) throw new InvalidOperationException("Interventions are unavailable.");
        return await interventionManager.HandleActionAsync(request);
    }

    public TaskProfile? CurrentTask => sessionManager?.CurrentTask;

    public IReadOnlyList<WorkspaceProfile> GetWorkspaces() => workspaceManager.GetWorkspaces();
    public WorkspaceProfile? GetActiveWorkspace() => workspaceManager.GetActiveWorkspace();
    public void AddWorkspace(WorkspaceProfile workspace) => workspaceManager.AddWorkspace(workspace);
    public void SetActiveWorkspace(string workspaceId)
    {
        workspaceManager.SetActiveWorkspace(workspaceId);
        interventionManager?.Reset();
        ClearAnalysis();
    }

    // Focus controls preserve task identity. Workspace is only the generic-session fallback.
    public void StartFocus(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        if (CurrentTask is null && workspaceManager.GetActiveWorkspace() is null)
            throw new InvalidOperationException("Select a workspace or task before starting focus.");
        interventionManager?.Reset();
        ClearAnalysis();
        focusManager.StartFocus(duration);
    }

    public void EndTask()
    {
        StopFocus();
        sessionManager?.ClearCurrentTask();
    }

    public void StartFocus(string workspaceId, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        workspaceManager.SetActiveWorkspace(workspaceId);
        StartFocus(duration);
    }

    public void PauseFocus()
    {
        timerManager.Pause();
        interventionManager?.Reset();
        ClearAnalysis();
    }
    public void ResumeFocus() => timerManager.Resume();
    public void StopFocus()
    {
        ClearAnalysis();
        focusManager.StopFocus();
    }
}
