using CluckIn.App.Interfaces;
using CluckIn.App.Models;
using CluckIn.App.Orchestration;

internal static class Program
{
    private static int _passed;

    private static async Task Main()
    {
        await RunAsync("enter focus is idempotent", EnterFocusIsIdempotentAsync);
        await RunAsync("partial startup failure cleans up", PartialStartupFailureCleansUpAsync);
        await RunAsync("non-urgent stores without notification", NonUrgentStoresWithoutNotificationAsync);
        await RunAsync("urgent off shows message only", UrgentOffShowsMessageOnlyAsync);
        await RunAsync("urgent suggestion shows suggestion", UrgentSuggestionShowsSuggestionAsync);
        await RunAsync("urgent on auto-replies once", UrgentOnAutoRepliesOnceAsync);
        await RunAsync("mode downgrade blocks in-flight auto reply", ModeDowngradeBlocksInFlightAutoReplyAsync);
        await RunAsync("stale old-session response is ignored", StaleOldSessionResponseIsIgnoredAsync);
        await RunAsync("PAT empty state skips AI summary", PatEmptyStateAsync);
        await RunAsync("PAT summarizes deferred messages", PatSummarizesDeferredMessagesAsync);
        await RunAsync("PAT summary is single-flight", PatSummaryIsSingleFlightAsync);
        await RunAsync("shutdown performs focus cleanup", ShutdownPerformsCleanupAsync);
        await RunAsync("invalid AI decision is rejected", InvalidDecisionIsRejectedAsync);

        await RunAsync("stale PAT summary is ignored", StaleSummaryAsync);
        await RunAsync("captured old listener callback is ignored", CapturedCallbackAsync);
        await RunAsync("intake order and failure isolation", OrderedIntakeAsync);
        await RunAsync("HTTP adapters reuse canonical contracts", HttpAdapterChecks.RunAsync);
        Console.WriteLine($"PASS: {_passed}/{_passed} Main Program smoke tests");
    }

    private static async Task RunAsync(string name, Func<Task> test)
    {
        await test();
        _passed++;
        Console.WriteLine($"PASS: {name}");
    }

    private static MainProgramCoordinator Create(
        FakeAIEngine ai,
        FakeMessageListener listener,
        FakeReplyService reply,
        FakeNotificationService notification,
        InMemoryFocusMessageStore? store = null) =>
        new(ai, listener, reply, notification, store ?? new());

    private static ExternalMessageContract Message(string id = "m1") => new()
    {
        Id = id,
        Source = "gmail",
        Sender = "sender@example.com",
        Title = "Test",
        Content = "hello",
        Timestamp = DateTimeOffset.UtcNow,
        Unread = true
    };

    private static AIDecisionContract Decision(
        ExternalMessageContract message,
        string value = "allow") => new()
    {
        MessageId = message.Id,
        Decision = value,
        Relevance = 0.5,
        Urgency = value == "urgent" ? 1.0 : 0.2,
        RequiresReply = true,
        Reason = "test"
    };

    private static void Check(bool condition, string message)
    {
        if(!condition){
            throw new InvalidOperationException(message);
        }
    }

    private static async Task EnterFocusIsIdempotentAsync()
    {
        var ai = new FakeAIEngine();
        var listener = new FakeMessageListener();
        var reply = new FakeReplyService();
        var notification = new FakeNotificationService();
        await using var coordinator = Create(ai, listener, reply, notification);

        var first = await coordinator.EnterFocusAsync();
        var entries = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => coordinator.EnterFocusAsync()));
        var second = entries[0];
        Check(entries.All(id => id == first), "Concurrent entries created different sessions.");

        Check(first == second, "Repeated enter must keep the same session.");
        Check(ai.StartCount == 1, "AI engine started more than once.");
        Check(listener.StartCount == 1, "Listener started more than once.");
        Check(coordinator.AIEngineRunning, "AI running state not set.");
        Check(coordinator.MessageListenerRunning, "Listener running state not set.");

        await coordinator.LeaveFocusAsync();
        await coordinator.LeaveFocusAsync();
        Check(ai.StopCount == 1, "AI engine did not stop exactly once.");
        Check(listener.StopCount == 1, "Listener did not stop exactly once.");
        Check(!coordinator.FocusSessionActive, "Focus session still active.");
    }

    private static async Task PartialStartupFailureCleansUpAsync()
    {
        var ai = new FakeAIEngine();
        var listener = new FakeMessageListener{StartException = new InvalidOperationException("listener failed")};
        var reply = new FakeReplyService();
        var notification = new FakeNotificationService();
        await using var coordinator = Create(ai, listener, reply, notification);

        var failed = false;
        try{
            await coordinator.EnterFocusAsync();
        }
        catch(InvalidOperationException){
            failed = true;
        }

        Check(failed, "Startup failure was not surfaced.");
        Check(ai.StartCount == 1, "AI engine was not started before listener failure.");
        Check(ai.StopCount == 1, "AI engine was not cleaned up after listener failure.");
        Check(listener.StopCount == 1, "Listener stop was not attempted after partial start failure.");
        Check(!coordinator.FocusSessionActive, "Half-started Focus session remained active.");
    }

    private static async Task NonUrgentStoresWithoutNotificationAsync()
    {
        var ai = new FakeAIEngine();
        var listener = new FakeMessageListener();
        var reply = new FakeReplyService();
        var notification = new FakeNotificationService();
        var store = new InMemoryFocusMessageStore();
        await using var coordinator = Create(ai, listener, reply, notification, store);
        var sessionId = await coordinator.EnterFocusAsync();
        var message = Message();
        ai.Analyze = (m, _, _) => Task.FromResult(Decision(m, "hold"));

        await listener.EmitAsync(message);

        Check(store.Snapshot(sessionId).Count == 1, "Deferred message was not stored.");
        Check(notification.TotalNotifications == 0, "Non-urgent message created a notification.");
        Check(reply.SendCount == 0, "Non-urgent message sent a reply.");
    }

    private static async Task UrgentOffShowsMessageOnlyAsync()
    {
        var ai = new FakeAIEngine();
        var listener = new FakeMessageListener();
        var reply = new FakeReplyService();
        var notification = new FakeNotificationService();
        var store = new InMemoryFocusMessageStore();
        await using var coordinator = Create(ai, listener, reply, notification, store);
        var sessionId = await coordinator.EnterFocusAsync();
        ai.Analyze = (m, _, _) => Task.FromResult(Decision(m, "urgent"));

        await listener.EmitAsync(Message());

        Check(notification.UrgentCount == 1, "Urgent OFF did not show message.");
        Check(notification.SuggestionCount == 0, "Urgent OFF showed a suggestion.");
        Check(ai.DraftCount == 0, "Urgent OFF generated an unnecessary draft.");
        Check(reply.SendCount == 0, "Urgent OFF sent a reply.");
        Check(store.Snapshot(sessionId).Count == 0, "Urgent OFF message entered the temporary list.");
    }

    private static async Task UrgentSuggestionShowsSuggestionAsync()
    {
        var ai = new FakeAIEngine();
        var listener = new FakeMessageListener();
        var reply = new FakeReplyService();
        var notification = new FakeNotificationService();
        var store = new InMemoryFocusMessageStore();
        await using var coordinator = Create(ai, listener, reply, notification, store);
        coordinator.SetAIAssistMode("suggestion");
        var sessionId = await coordinator.EnterFocusAsync();
        ai.Analyze = (m, _, _) => Task.FromResult(Decision(m, "urgent"));
        ai.Draft = (_, _, _, _) => Task.FromResult<string?>("suggested reply");

        await listener.EmitAsync(Message());

        Check(notification.SuggestionCount == 1, "Suggestion mode did not show suggestion.");
        Check(reply.SendCount == 0, "Suggestion mode sent a reply.");
        Check(store.Snapshot(sessionId).Count == 0, "Urgent suggestion message entered the temporary list.");
    }

    private static async Task UrgentOnAutoRepliesOnceAsync()
    {
        var ai = new FakeAIEngine();
        var listener = new FakeMessageListener();
        var reply = new FakeReplyService();
        var notification = new FakeNotificationService();
        var store = new InMemoryFocusMessageStore();
        await using var coordinator = Create(ai, listener, reply, notification, store);
        coordinator.SetAIAssistMode("on");
        var sessionId = await coordinator.EnterFocusAsync();
        ai.Analyze = (m, _, _) => Task.FromResult(Decision(m, "urgent"));
        ai.Draft = (_, _, _, _) => Task.FromResult<string?>("auto reply");
        var message = Message();

        await listener.EmitAsync(message);
        await listener.EmitAsync(message);

        Check(reply.SendCount == 1, "Duplicate message triggered duplicate auto reply.");
        Check(notification.TotalNotifications == 0, "Auto mode unexpectedly notified.");
        Check(store.Snapshot(sessionId).Count == 0, "Urgent auto-reply message entered the temporary list.");
    }

    private static async Task ModeDowngradeBlocksInFlightAutoReplyAsync()
    {
        var ai = new FakeAIEngine();
        var listener = new FakeMessageListener();
        var reply = new FakeReplyService();
        var notification = new FakeNotificationService();
        await using var coordinator = Create(ai, listener, reply, notification);
        coordinator.SetAIAssistMode("on");
        await coordinator.EnterFocusAsync();
        ai.Analyze = (m, _, _) => Task.FromResult(Decision(m, "urgent"));

        var draftEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDraft = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ai.Draft = async (_, _, _, _) =>{
            draftEntered.TrySetResult(true);
            return await releaseDraft.Task;
        };

        var inFlight = listener.EmitAsync(Message());
        await draftEntered.Task;
        coordinator.SetAIAssistMode("off");
        releaseDraft.TrySetResult("should not auto-send");
        await inFlight;

        Check(reply.SendCount == 0, "Turning automation off did not stop in-flight auto reply.");
        Check(notification.UrgentCount == 1, "Downgraded request did not fall back to message notification.");
    }

    private static async Task StaleOldSessionResponseIsIgnoredAsync()
    {
        var ai = new FakeAIEngine();
        var listener = new FakeMessageListener();
        var reply = new FakeReplyService();
        var notification = new FakeNotificationService();
        var store = new InMemoryFocusMessageStore();
        await using var coordinator = Create(ai, listener, reply, notification, store);
        var oldSession = await coordinator.EnterFocusAsync();

        var analyzeEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAnalyze = new TaskCompletionSource<AIDecisionContract>(TaskCreationOptions.RunContinuationsAsynchronously);
        ai.Analyze = async (m, _, _) =>{
            analyzeEntered.TrySetResult(true);
            return await releaseAnalyze.Task;
        };

        var message = Message("old-message");
        var oldWork = listener.EmitAsync(message);
        await analyzeEntered.Task;
        await coordinator.LeaveFocusAsync();
        var newSession = await coordinator.EnterFocusAsync();
        Check(oldSession != newSession, "New Focus did not create a fresh session id.");

        releaseAnalyze.TrySetResult(Decision(message, "hold"));
        await oldWork;

        Check(store.Snapshot(newSession).Count == 0, "Old response leaked into new session.");
        Check(notification.TotalNotifications == 0, "Old response generated a notification.");
        Check(reply.SendCount == 0, "Old response sent a reply.");
    }

    private static async Task PatEmptyStateAsync()
    {
        var ai = new FakeAIEngine();
        var listener = new FakeMessageListener();
        var reply = new FakeReplyService();
        var notification = new FakeNotificationService();
        await using var coordinator = Create(ai, listener, reply, notification);
        await coordinator.EnterFocusAsync();

        await coordinator.HandlePatAsync();

        Check(ai.SummaryCount == 0, "Empty PAT request unnecessarily called AI summary.");
        Check(notification.EmptySummaryCount == 1, "Empty PAT state was not shown.");
    }

    private static async Task PatSummarizesDeferredMessagesAsync()
    {
        var ai = new FakeAIEngine();
        var listener = new FakeMessageListener();
        var reply = new FakeReplyService();
        var notification = new FakeNotificationService();
        await using var coordinator = Create(ai, listener, reply, notification);
        await coordinator.EnterFocusAsync();
        ai.Analyze = (m, _, _) => Task.FromResult(Decision(m, m.Id == "urgent" ? "urgent" : "hold"));
        ai.Summary = (messages, _, _) => {
            Check(messages.Select(record => record.Message.Id).SequenceEqual(["m1"]),
                "PAT included an urgent message in the temporary list.");
            return Task.FromResult("summary");
        };

        await listener.EmitAsync(Message());
        await listener.EmitAsync(Message("urgent"));
        await coordinator.HandlePatAsync();

        Check(ai.SummaryCount == 1, "PAT did not invoke summary.");
        Check(notification.SummaryCount == 1, "PAT summary was not displayed.");
    }

    private static async Task PatSummaryIsSingleFlightAsync()
    {
        var ai = new FakeAIEngine();
        var listener = new FakeMessageListener();
        var reply = new FakeReplyService();
        var notification = new FakeNotificationService();
        await using var coordinator = Create(ai, listener, reply, notification);
        await coordinator.EnterFocusAsync();
        ai.Analyze = (m, _, _) => Task.FromResult(Decision(m, "hold"));
        await listener.EmitAsync(Message());

        var summaryEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSummary = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        ai.Summary = async (_, _, _) =>{
            summaryEntered.TrySetResult(true);
            return await releaseSummary.Task;
        };

        var first = coordinator.HandlePatAsync();
        await summaryEntered.Task;
        var second = coordinator.HandlePatAsync();
        await second;
        releaseSummary.TrySetResult("summary");
        await first;

        Check(ai.SummaryCount == 1, "Concurrent PAT sent duplicate summary requests.");
        Check(notification.SummaryCount == 1, "Single summary was not displayed exactly once.");
    }

    private static async Task ShutdownPerformsCleanupAsync()
    {
        var ai = new FakeAIEngine();
        var listener = new FakeMessageListener();
        var reply = new FakeReplyService();
        var notification = new FakeNotificationService();
        await using var coordinator = Create(ai, listener, reply, notification);
        await coordinator.EnterFocusAsync();

        await coordinator.ShutdownAsync();
        await coordinator.ShutdownAsync();

        Check(ai.StopCount == 1, "Shutdown did not stop AI engine.");
        Check(listener.StopCount == 1, "Shutdown did not stop listener.");
        Check(!coordinator.FocusSessionActive, "Shutdown left Focus active.");
    }

    private static async Task InvalidDecisionIsRejectedAsync()
    {
        var ai = new FakeAIEngine();
        var listener = new FakeMessageListener();
        var reply = new FakeReplyService();
        var notification = new FakeNotificationService();
        await using var coordinator = Create(ai, listener, reply, notification);
        await coordinator.EnterFocusAsync();
        var errors = 0;
        coordinator.BackgroundError += _ => errors++;
        ai.Analyze = (_, _, _) => Task.FromResult(new AIDecisionContract{
            MessageId = "wrong-id",
            Decision = "urgent",
            Relevance = 0.5,
            Urgency = 1.0,
            RequiresReply = true,
        Reason = "test"
        });

        await listener.EmitAsync(Message());

        Check(errors == 1, "Invalid AI decision did not surface an error.");
        Check(notification.TotalNotifications == 0, "Invalid decision was routed to UI.");
        Check(reply.SendCount == 0, "Invalid decision sent a reply.");
    }

    private static async Task StaleSummaryAsync()
    {
        var ai = new FakeAIEngine();
        var listener = new FakeMessageListener();
        var notification = new FakeNotificationService();
        await using var coordinator = Create(ai, listener, new(), notification);
        await coordinator.HandlePatAsync();
        Check(notification.TotalNotifications == 0, "PAT outside Focus displayed a summary.");
        await coordinator.EnterFocusAsync();
        await listener.EmitAsync(Message());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        ai.Summary = (_, _, _) => { entered.SetResult(); return release.Task; };
        var pat = coordinator.HandlePatAsync();
        await entered.Task;
        await coordinator.LeaveFocusAsync();
        await coordinator.EnterFocusAsync();
        release.SetResult("stale summary");
        await pat;
        Check(notification.TotalNotifications == 0, "Old summary leaked into a new session.");
    }

    private static async Task CapturedCallbackAsync()
    {
        var ai = new FakeAIEngine();
        var listener = new FakeMessageListener();
        var store = new InMemoryFocusMessageStore();
        await using var coordinator = Create(ai, listener, new(), new(), store);
        var oldId = await coordinator.EnterFocusAsync();
        var oldCallback = listener.Capture();
        await listener.EmitAsync(Message());
        await coordinator.LeaveFocusAsync();
        Check(store.Snapshot(oldId).Count == 0, "Leaving Focus did not free messages.");
        var id = await coordinator.EnterFocusAsync();
        await oldCallback!(Message("late"));
        Check(ai.AnalyzeCount == 1 && store.Snapshot(id).Count == 0, "Captured callback entered new session.");
    }

    private static async Task OrderedIntakeAsync()
    {
        var ai = new FakeAIEngine();
        var listener = new FakeMessageListener();
        var store = new InMemoryFocusMessageStore();
        var notification = new FakeNotificationService();
        await using var coordinator = Create(ai, listener, new(), notification, store);
        var id = await coordinator.EnterFocusAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = 0;
        coordinator.BackgroundError += _ => errors++;
        ai.Analyze = async (m, _, _) => {
            if (m.Id == "first") {
                entered.SetResult();
                await release.Task;
                throw new InvalidOperationException("one bad message");
            }
            return Decision(m, "hold");
        };
        var first = listener.EmitAsync(Message("first") with { Timestamp = DateTimeOffset.UtcNow.AddDays(1) });
        await entered.Task;
        Check(store.Snapshot(id).Count == 0, "Message was stored before analysis completed.");
        var second = listener.EmitAsync(Message("second"));
        var third = listener.EmitAsync(Message("third") with { Timestamp = DateTimeOffset.UtcNow.AddDays(-1) });
        Check(ai.AnalyzeCount == 1, "Analysis was not serialized.");
        release.SetResult();
        await Task.WhenAll(first, second, third);
        Check(errors == 1 && ai.AnalyzeCount == 3, "One failure killed subsequent message analysis.");
        Check(notification.TotalNotifications == 0, "Non-urgent messages created a notification.");
        var stored = store.Snapshot(id);
        Check(stored.Select(m => m.Message.Id).SequenceEqual(["second", "third"]),
            "Temporary list included a failed analysis or lost non-urgent intake order.");
        Check(stored.All(record => record.Decision is not null),
            "Temporary list contained an incomplete inference result.");
    }

    private sealed class FakeAIEngine : IFocusAIEngine
    {
        public int StartCount {get; private set;}
        public int StopCount {get; private set;}
        public int AnalyzeCount {get; private set;}
        public int DraftCount {get; private set;}
        public int SummaryCount {get; private set;}

        public Func<ExternalMessageContract, string, CancellationToken, Task<AIDecisionContract>>? Analyze {get; set;}
        public Func<ExternalMessageContract, AIDecisionContract, string, CancellationToken, Task<string?>>? Draft {get; set;}
        public Func<IReadOnlyList<FocusMessageRecord>, string, CancellationToken, Task<string>>? Summary {get; set;}

        public Task StartAsync(string focusSessionId, CancellationToken cancellationToken)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public Task<AIDecisionContract> AnalyzeMessageAsync(
            ExternalMessageContract message,
            string focusSessionId,
            CancellationToken cancellationToken)
        {
            AnalyzeCount++;
            return Analyze?.Invoke(message, focusSessionId, cancellationToken) ??
                Task.FromResult(Decision(message));
        }

        public Task<string?> DraftReplyAsync(
            ExternalMessageContract message,
            AIDecisionContract decision,
            string focusSessionId,
            CancellationToken cancellationToken)
        {
            DraftCount++;
            return Draft?.Invoke(message, decision, focusSessionId, cancellationToken) ??
                Task.FromResult<string?>("reply");
        }

        public Task<string> SummarizeAsync(
            IReadOnlyList<FocusMessageRecord> messages,
            string focusSessionId,
            CancellationToken cancellationToken)
        {
            SummaryCount++;
            return Summary?.Invoke(messages, focusSessionId, cancellationToken) ??
                Task.FromResult("summary");
        }
    }

    private sealed class FakeMessageListener : IMessageListener
    {
        public event Func<ExternalMessageContract, Task>? MessageReceived;
        public Func<ExternalMessageContract, Task>? Capture() => MessageReceived;
        public int StartCount {get; private set;}
        public int StopCount {get; private set;}
        public Exception? StartException {get; init;}

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            if(StartException is not null){
                throw StartException;
            }
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public async Task EmitAsync(ExternalMessageContract message)
        {
            var handlers = MessageReceived?.GetInvocationList() ?? [];
            foreach(var handler in handlers){
                await ((Func<ExternalMessageContract, Task>)handler)(message);
            }
        }
    }

    private sealed class FakeReplyService : IExternalReplyService
    {
        public int SendCount {get; private set;}

        public Task<ExternalReplyResult> SendReplyAsync(
            ExternalMessageContract message,
            string reply,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(new ExternalReplyResult{Success = true});
        }
    }

    private sealed class FakeNotificationService : IUserNotificationService
    {
        public int UrgentCount {get; private set;}
        public int SuggestionCount {get; private set;}
        public int SummaryCount {get; private set;}
        public int EmptySummaryCount {get; private set;}
        public int TotalNotifications =>
            UrgentCount + SuggestionCount + SummaryCount + EmptySummaryCount;

        public Task ShowUrgentMessageAsync(
            ExternalMessageContract message,
            CancellationToken cancellationToken)
        {
            UrgentCount++;
            return Task.CompletedTask;
        }

        public Task ShowUrgentMessageWithSuggestionAsync(
            ExternalMessageContract message,
            string suggestion,
            CancellationToken cancellationToken)
        {
            SuggestionCount++;
            return Task.CompletedTask;
        }

        public Task ShowSummaryAsync(
            string summary,
            bool isEmpty,
            CancellationToken cancellationToken)
        {
            if(isEmpty){
                EmptySummaryCount++;
            }
            else{
                SummaryCount++;
            }
            return Task.CompletedTask;
        }
    }
}
