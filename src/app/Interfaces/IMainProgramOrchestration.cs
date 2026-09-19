using CluckIn.App.Models;

namespace CluckIn.App.Interfaces;

public interface IFocusAIEngine
{
    void SetAutomationMode(AIAssistRoutingMode mode) { }
    Task StartAsync(string focusSessionId, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task<AIDecisionContract> AnalyzeMessageAsync(
        ExternalMessageContract message,
        string focusSessionId,
        CancellationToken cancellationToken);
    Task<string?> DraftReplyAsync(
        ExternalMessageContract message,
        AIDecisionContract decision,
        string focusSessionId,
        CancellationToken cancellationToken);
    Task<string> SummarizeAsync(
        IReadOnlyList<FocusMessageRecord> messages,
        string focusSessionId,
        CancellationToken cancellationToken);
}

public interface IMessageListener
{
    event Func<ExternalMessageContract, Task>? MessageReceived;
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public interface IExternalReplyService
{
    Task<ExternalReplyResult> SendReplyAsync(
        ExternalMessageContract message,
        string reply,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

public interface IUserNotificationService
{
    Task ShowUrgentMessageAsync(
        ExternalMessageContract message,
        CancellationToken cancellationToken);
    Task ShowUrgentMessageWithSuggestionAsync(
        ExternalMessageContract message,
        string suggestion,
        CancellationToken cancellationToken);
    Task ShowSummaryAsync(
        string summary,
        bool isEmpty,
        CancellationToken cancellationToken);
}

public interface IFocusMessageStore
{
    void CreateSession(string focusSessionId);
    void Add(string focusSessionId, FocusMessageRecord record);
    IReadOnlyList<FocusMessageRecord> Snapshot(string focusSessionId);
    void FreeSession(string focusSessionId);
}
