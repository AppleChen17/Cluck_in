using CluckIn.App.Interfaces;
using CluckIn.App.Models;

namespace CluckIn.App.Orchestration;

public sealed class MainProgramCoordinator : IAsyncDisposable
{
    private sealed class ActiveFocusSession(
        string id,
        CancellationTokenSource cancellation)
    {
        public string Id {get;} = id;
        public CancellationTokenSource Cancellation {get;} = cancellation;
        public HashSet<string> AutoReplyDispatches {get;} =
            new(StringComparer.Ordinal);
        public object ReplyGate {get;} = new();
        public SemaphoreSlim IntakeGate {get;} = new(1, 1);
        public HashSet<string> Received {get;} = new(StringComparer.Ordinal);
        public Func<ExternalMessageContract, Task>? Handler {get; set;}
        public CancellationToken Token {get;} = cancellation.Token;
    }

    private readonly IFocusAIEngine _aiEngine;
    private readonly IMessageListener _messageListener;
    private readonly IExternalReplyService _externalReplyService;
    private readonly IUserNotificationService _notificationService;
    private readonly IFocusMessageStore _messageStore;
    private readonly TimeProvider _clock;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _summaryGate = new(1, 1);
    private readonly object _modeGate = new();

    private ActiveFocusSession? _activeSession;
    private AIAssistRoutingMode _aiAssistMode = AIAssistRoutingMode.Off;
    private int _aiEngineRunning;
    private int _messageListenerRunning;
    private bool _disposed;

    public MainProgramCoordinator(
        IFocusAIEngine aiEngine,
        IMessageListener messageListener,
        IExternalReplyService externalReplyService,
        IUserNotificationService notificationService,
        IFocusMessageStore messageStore,
        TimeProvider? timeProvider = null,
        Action<string>? log = null)
    {
        _aiEngine = aiEngine ?? throw new ArgumentNullException(nameof(aiEngine));
        _messageListener = messageListener ??
            throw new ArgumentNullException(nameof(messageListener));
        _externalReplyService = externalReplyService ??
            throw new ArgumentNullException(nameof(externalReplyService));
        _notificationService = notificationService ??
            throw new ArgumentNullException(nameof(notificationService));
        _messageStore = messageStore ??
            throw new ArgumentNullException(nameof(messageStore));
        _clock = timeProvider ?? TimeProvider.System;
        _log = log;
    }

    public event Action<Exception>? BackgroundError;
    public event Action<ExternalMessageContract, ExternalReplyResult>?
        AutoReplyCompleted;

    public bool FocusSessionActive =>
        Volatile.Read(ref _activeSession) is not null;

    public string? FocusSessionId =>
        Volatile.Read(ref _activeSession)?.Id;

    public bool AIEngineRunning => Volatile.Read(ref _aiEngineRunning) != 0;

    public bool MessageListenerRunning =>
        Volatile.Read(ref _messageListenerRunning) != 0;

    public AIAssistRoutingMode CurrentAIAssistMode
    {
        get{
            lock(_modeGate){
                return _aiAssistMode;
            }
        }
    }

    public void SetAIAssistMode(AIAssistRoutingMode mode)
    {
        lock(_modeGate){
            _aiAssistMode = mode;
            _aiEngine.SetAutomationMode(mode);
        }
        _log?.Invoke($"AI assist routing mode changed to {mode}");
    }

    public void SetAIAssistMode(string mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        SetAIAssistMode(mode.Trim().ToLowerInvariant() switch{
            "off" => AIAssistRoutingMode.Off,
            "suggestion" => AIAssistRoutingMode.Suggestion,
            "on" => AIAssistRoutingMode.On,
            _ => throw new ArgumentOutOfRangeException(
                nameof(mode),
                mode,
                "AI assist mode must be off, suggestion, or on."
            )
        });
    }

    public async Task<string> EnterFocusAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try{
            ThrowIfDisposed();
            var current = Volatile.Read(ref _activeSession);
            if(current is not null){
                return current.Id;
            }

            var sessionId = Guid.NewGuid().ToString("N");
            cancellationToken.ThrowIfCancellationRequested();
            var sessionCancellation = new CancellationTokenSource();
            using var startupCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    sessionCancellation.Token
                );
            var session = new ActiveFocusSession(sessionId, sessionCancellation);
            var aiStartAttempted = false;
            var listenerSubscribed = false;
            var listenerStartAttempted = false;

            try{
                _messageStore.CreateSession(sessionId);

                aiStartAttempted = true;
                await _aiEngine.StartAsync(
                    sessionId,
                    startupCancellation.Token
                ).ConfigureAwait(false);
                Volatile.Write(ref _aiEngineRunning, 1);
                cancellationToken.ThrowIfCancellationRequested();

                session.Handler = message => OnMessageReceivedAsync(session, message);
                _messageListener.MessageReceived += session.Handler;
                listenerSubscribed = true;

                Volatile.Write(ref _activeSession, session);

                listenerStartAttempted = true;
                await _messageListener.StartAsync(
                    startupCancellation.Token
                ).ConfigureAwait(false);
                Volatile.Write(ref _messageListenerRunning, 1);
                cancellationToken.ThrowIfCancellationRequested();

                _log?.Invoke($"Focus orchestration started: {sessionId}");
                return sessionId;
            }
            catch{
                Volatile.Write(ref _activeSession, null);
                sessionCancellation.Cancel();

                if(listenerSubscribed){
                    _messageListener.MessageReceived -= session.Handler;
                }

                if(listenerStartAttempted){
                    await TryCleanupAsync(async () =>{
                        await _messageListener.StopAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                        Volatile.Write(ref _messageListenerRunning, 0);
                    }).ConfigureAwait(false);
                }

                if(aiStartAttempted){
                    await TryCleanupAsync(async () =>{
                        await _aiEngine.StopAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                        Volatile.Write(ref _aiEngineRunning, 0);
                    }).ConfigureAwait(false);
                }

                TryFreeSession(sessionId);
                sessionCancellation.Dispose();
                throw;
            }
        }
        finally{
            _lifecycleGate.Release();
        }
    }

    public Task LeaveFocusAsync(CancellationToken cancellationToken = default) =>
        LeaveFocusCoreAsync(false, cancellationToken);

    private async Task LeaveFocusCoreAsync(bool dispose, CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try{
            if(dispose) _disposed = true;
            var session = Volatile.Read(ref _activeSession);
            if(session is null){
                return;
            }

            Volatile.Write(ref _activeSession, null);
            _messageListener.MessageReceived -= session.Handler;
            session.Cancellation.Cancel();

            var errors = new List<Exception>();

            if(MessageListenerRunning){
                await CaptureCleanupErrorAsync(async () =>{
                    await _messageListener.StopAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    Volatile.Write(ref _messageListenerRunning, 0);
                }, errors).ConfigureAwait(false);
            }

            if(AIEngineRunning){
                await CaptureCleanupErrorAsync(async () =>{
                    await _aiEngine.StopAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    Volatile.Write(ref _aiEngineRunning, 0);
                }, errors).ConfigureAwait(false);
            }

            try{
                _messageStore.FreeSession(session.Id);
            }
            catch(Exception ex){
                errors.Add(ex);
                ReportBackgroundError(ex);
            }
            finally{
                session.Cancellation.Dispose();
            }

            _log?.Invoke($"Focus orchestration stopped: {session.Id}");

            if(errors.Count > 0){
                throw new AggregateException(
                    "One or more Focus cleanup steps failed.",
                    errors
                );
            }
        }
        finally{
            _lifecycleGate.Release();
        }
    }

    public async Task HandlePatAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if(!await _summaryGate.WaitAsync(0, cancellationToken)
            .ConfigureAwait(false))
        {
            return;
        }

        try{
            var session = Volatile.Read(ref _activeSession);
            if(session is null){
                return;
            }

            using var linkedCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    session.Token
                );

            var messages = _messageStore.Snapshot(session.Id);
            if(messages.Count == 0){
                if(!IsCurrentSession(session)){
                    return;
                }

                await _notificationService.ShowSummaryAsync(
                    "No messages in this Focus session.",
                    true,
                    session.Token
                ).ConfigureAwait(false);
                return;
            }

            var summary = await _aiEngine.SummarizeAsync(
                messages,
                session.Id,
                linkedCancellation.Token
            ).ConfigureAwait(false);

            if(!IsCurrentSession(session)){
                return;
            }

            if(string.IsNullOrWhiteSpace(summary)){
                throw new InvalidOperationException("AI summary was empty.");
            }

            await _notificationService.ShowSummaryAsync(
                summary,
                false,
                session.Token
            ).ConfigureAwait(false);
        }
        catch(OperationCanceledException){
            if(cancellationToken.IsCancellationRequested){
                throw;
            }
        }
        catch(Exception ex){
            ReportBackgroundError(ex);
        }
        finally{
            _summaryGate.Release();
        }
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default) =>
        LeaveFocusAsync(cancellationToken);

    private async Task OnMessageReceivedAsync(ActiveFocusSession session, ExternalMessageContract message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if(!IsCurrentSession(session)) return;
        var acquired = false;
        try{
            await session.IntakeGate.WaitAsync(session.Token).ConfigureAwait(false);
            acquired = true;
            if(!IsCurrentSession(session) || !session.Received.Add(message.Id)) return;
            var initialMode = CurrentAIAssistMode;
            var decision = await _aiEngine.AnalyzeMessageAsync(
                message,
                session.Id,
                session.Token
            ).ConfigureAwait(false);

            if(!IsCurrentSession(session)){
                return;
            }

            ValidateDecision(message, decision);

            if(!decision.IsUrgent){
                _messageStore.Add(session.Id, new FocusMessageRecord {
                    Message = message,
                    Decision = decision,
                    ReplySuggestion = decision.ReplyDraft,
                    AnalysisTimestamp = _clock.GetUtcNow()
                });
                return;
            }

            var routingMode = GetNonEscalatingMode(initialMode, CurrentAIAssistMode);
            if(routingMode == AIAssistRoutingMode.Off || !decision.RequiresReply){
                await _notificationService.ShowUrgentMessageAsync(
                    message,
                    session.Token
                ).ConfigureAwait(false);
                return;
            }

            var suggestion = await _aiEngine.DraftReplyAsync(
                message,
                decision,
                session.Id,
                session.Token
            ).ConfigureAwait(false);

            if(!IsCurrentSession(session)){
                return;
            }

            if(string.IsNullOrWhiteSpace(suggestion)){
                await _notificationService.ShowUrgentMessageAsync(message, session.Token).ConfigureAwait(false);
                throw new InvalidOperationException(
                    "AI draft reply was empty for an urgent message."
                );
            }

            // A user turning automation down while a draft is in flight must take
            // effect immediately. A mid-flight change may downgrade behavior, but
            // it never upgrades Suggestion to automatic sending for this message.
            routingMode = GetNonEscalatingMode(
                routingMode,
                CurrentAIAssistMode
            );

            if(routingMode == AIAssistRoutingMode.Off || !decision.RequiresReply){
                await _notificationService.ShowUrgentMessageAsync(
                    message,
                    session.Token
                ).ConfigureAwait(false);
                return;
            }

            if(routingMode == AIAssistRoutingMode.Suggestion){
                await _notificationService.ShowUrgentMessageWithSuggestionAsync(
                    message,
                    suggestion,
                    session.Token
                ).ConfigureAwait(false);
                return;
            }

            if(!TryBeginAutoReply(session, message.Id)){
                return;
            }

            if(!IsCurrentSession(session)){
                return;
            }

            var result = await _externalReplyService.SendReplyAsync(
                message,
                suggestion,
                $"{session.Id}:{message.Id}",
                session.Token
            ).ConfigureAwait(false);

            if(IsCurrentSession(session)){
                AutoReplyCompleted?.Invoke(message, result);
            }

            if(!result.Success){
                throw new InvalidOperationException(
                    result.Error ?? "External auto reply failed."
                );
            }
        }
        catch(OperationCanceledException) when(session.Token.IsCancellationRequested){
        }
        catch(Exception ex){
            ReportBackgroundError(ex);
        }
        finally {
            if(acquired) session.IntakeGate.Release();
        }
    }

    private bool IsCurrentSession(ActiveFocusSession session) =>
        ReferenceEquals(Volatile.Read(ref _activeSession), session) &&
        !session.Token.IsCancellationRequested;

    private static AIAssistRoutingMode GetNonEscalatingMode(
        AIAssistRoutingMode initial,
        AIAssistRoutingMode current) =>
        (AIAssistRoutingMode)Math.Min((int)initial, (int)current);

    private static void ValidateDecision(
        ExternalMessageContract message,
        AIDecisionContract decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if(!string.Equals(decision.MessageId, message.Id, StringComparison.Ordinal)){
            throw new InvalidOperationException(
                "AI decision messageId does not match the incoming message id."
            );
        }

        if(decision.Decision is not ("urgent" or "allow" or "hold")){
            throw new InvalidOperationException(
                $"Unsupported AI decision value: {decision.Decision}"
            );
        }

        if(decision.Relevance is < 0 or > 1 || decision.Urgency is < 0 or > 1){
            throw new InvalidOperationException(
                "AI decision relevance and urgency must be between 0 and 1."
            );
        }

        if(string.IsNullOrWhiteSpace(decision.Reason)){
            throw new InvalidOperationException("AI decision reason is required.");
        }
    }

    private static bool TryBeginAutoReply(
        ActiveFocusSession session,
        string messageId)
    {
        lock(session.ReplyGate){
            return session.AutoReplyDispatches.Add(messageId);
        }
    }

    private void TryFreeSession(string sessionId)
    {
        try{
            _messageStore.FreeSession(sessionId);
        }
        catch(Exception ex){
            ReportBackgroundError(ex);
        }
    }

    private void ReportBackgroundError(Exception exception)
    {
        _log?.Invoke(exception.ToString());
        BackgroundError?.Invoke(exception);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private async Task TryCleanupAsync(Func<Task> cleanup)
    {
        try{
            await cleanup().ConfigureAwait(false);
        }
        catch(Exception ex){
            ReportBackgroundError(ex);
        }
    }

    private async Task CaptureCleanupErrorAsync(
        Func<Task> cleanup,
        ICollection<Exception> errors)
    {
        try{
            await cleanup().ConfigureAwait(false);
        }
        catch(Exception ex){
            errors.Add(ex);
            ReportBackgroundError(ex);
        }
    }

    public ValueTask DisposeAsync() => new(LeaveFocusCoreAsync(true, CancellationToken.None));
}
