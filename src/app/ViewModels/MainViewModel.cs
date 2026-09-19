using System.ComponentModel;
using System.Runtime.CompilerServices;
using CluckIn.App.Models;
using CluckIn.App.Services;

namespace CluckIn.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly DesktopAgentService _agent;
    private readonly SemaphoreSlim _serviceGate = new(1, 1);
    private DesktopContext _context = new();
    private FocusEvaluation _evaluation = new() { Reason = "Focus Mode is disabled." };
    private WorkspaceProfile? _selectedWorkspace;
    private bool _busy;
    private bool _closing;
    private string? _errorMessage;
    private string? _interventionError;
    private readonly CancellationTokenSource _messageCancellation = new();

    public MainViewModel(DesktopAgentService agent)
    {
        _agent = agent;
        PreviewUrgentMessageCommand = new(_ =>
        {
            _agent.UrgentMessages?.Enqueue(new(new ExternalMessage
            {
                Id = "preview:" + Guid.NewGuid(), Source = "slack", Sender = "Cluck In 測試",
                Title = "WPF 通知預覽（非真實訊息）", Content = "看到這個視窗，表示 WPF 緊急通知可以正常顯示。",
                Timestamp = DateTimeOffset.Now
            }, "這是本機視窗測試，不需要訊息或 AI 服務。"));
            OnPropertyChanged(string.Empty);
            return Task.CompletedTask;
        }, _ => !_closing && _agent.UrgentMessages is { Count: < 100 });
        AcknowledgeUrgentMessageCommand = new(_ =>
        {
            if (UrgentMessage is { } alert) _agent.UrgentMessages?.Acknowledge(alert.Message.Id);
            OnPropertyChanged(string.Empty);
            return Task.CompletedTask;
        });
        Workspaces = agent.GetWorkspaces();
        _selectedWorkspace = agent.GetActiveWorkspace() ?? Workspaces.FirstOrDefault();
        if (agent.GetActiveWorkspace() is null && _selectedWorkspace is not null)
            agent.SetActiveWorkspace(_selectedWorkspace.Id);

        ReturnToWorkCommand = new(_ => RunInterventionAsync(InterventionAction.ReturnToWork),
            _ => CanInteract && Intervention.IsActive && Intervention.CanReturnToWork);
        TemporaryAllowCommand = new(_ => RunInterventionAsync(InterventionAction.TemporaryAllow),
            _ => CanInteract && Intervention.IsActive);
        StartFocusCommand = new(_ => RunAsync(() => _agent.StartFocus(TimeSpan.FromMinutes(_agent.CurrentTask?.FocusDurationMinutes ?? 25))),
            _ => CanInteract && (_agent.CurrentTask is not null || SelectedWorkspace is not null) && !IsFocusModeEnabled);
        EndTaskCommand = new(_ => RunAsync(_agent.EndTask), _ => CanInteract && _agent.CurrentTask is not null);
        PauseFocusCommand = new(_ => RunAsync(_agent.PauseFocus),
            _ => CanInteract && _context.FocusSession.Status == FocusSessionStatus.Running);
        ResumeFocusCommand = new(_ => RunAsync(_agent.ResumeFocus),
            _ => CanInteract && _context.FocusSession.Status == FocusSessionStatus.Paused);
        StopFocusCommand = new(_ => RunAsync(_agent.StopFocus), _ => CanInteract && IsFocusModeEnabled);
        ChangeWorkspaceCommand = new(parameter => parameter is WorkspaceProfile workspace
                ? RunAsync(() => _agent.SetActiveWorkspace(workspace.Id)) : Task.CompletedTask,
            parameter => CanInteract && parameter is WorkspaceProfile);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public UrgentMessage? UrgentMessage => _agent.UrgentMessages?.Current;
    public int UrgentMessageCount => _agent.UrgentMessages?.Count ?? 0;
    public string UrgentMessageStatus => _agent.UrgentMessages?.Status ?? "";
    public RelayCommand AcknowledgeUrgentMessageCommand { get; }
    public RelayCommand PreviewUrgentMessageCommand { get; }
    public async Task PollUrgentMessagesAsync()
    {
        if (_closing) return;
        await _agent.PollUrgentMessagesAsync(_messageCancellation.Token);
        if (!_closing) OnPropertyChanged(string.Empty);
    }
    public IReadOnlyList<WorkspaceProfile> Workspaces { get; }
    public WorkspaceProfile? SelectedWorkspace
    {
        get => _selectedWorkspace;
        set
        {
            if (value is null || value == _selectedWorkspace || !ChangeWorkspaceCommand.CanExecute(value)) return;
            // The service result commits selection, so a failed change cannot leave a stale UI choice.
            ChangeWorkspaceCommand.Execute(value);
        }
    }

    public string? InterventionErrorMessage => _interventionError;
    public InterventionState Intervention => _agent.Intervention;
    public RelayCommand ReturnToWorkCommand { get; }
    public RelayCommand TemporaryAllowCommand { get; }
    public string CurrentTaskName => _agent.CurrentTask?.Name ?? "No task · Generic focus";
    public RelayCommand EndTaskCommand { get; }
    public string WorkspaceName => _context.WorkspaceName ?? _selectedWorkspace?.Name ?? "No workspace";
    public string ActiveApplication => Display(_context.ActiveWindow.ProcessName);
    public string ActiveWindowTitle => Display(_context.ActiveWindow.WindowTitle);
    public string BrowserPageTitle => Display(_context.Browser?.PageTitle);
    public string BrowserUrl => _context.Browser is null ? "—" : _context.Browser.Url ?? "URL unavailable";
    public string FocusStatus => !_evaluation.IsEvaluated ? "Neutral" : _evaluation.IsFocused ? "Focused" : "Distracted";
    public string FocusReason => _evaluation.Reason;
    public bool IsFocusModeEnabled => _context.FocusModeEnabled;
    public string SessionStatus => _context.FocusSession.Status.ToString();
    public string TimerText
    {
        get
        {
            var seconds = (long)Math.Ceiling(_context.FocusSession.RemainingTime.TotalSeconds);
            return $"{seconds / 60:00}:{seconds % 60:00}";
        }
    }
    public bool CanInteract => !_busy && !_closing;
    public string? ErrorMessage => _errorMessage;
    public RelayCommand StartFocusCommand { get; }
    public RelayCommand PauseFocusCommand { get; }
    public RelayCommand ResumeFocusCommand { get; }
    public RelayCommand StopFocusCommand { get; }
    public RelayCommand ChangeWorkspaceCommand { get; }

    // Called on the dispatcher. Await returns here before publishing bound properties.
    public Task RefreshAsync() => RunAsync();

    private async Task RunAsync(Action? action = null)
    {
        if (_closing) return;
        if (action is not null)
        {
            // User commands queue behind a refresh; polling must not disable the
            // ComboBox every second and dismiss its open dropdown.
            _busy = true;
            NotifyCommands();
            await _serviceGate.WaitAsync();
        }
        else if (!await _serviceGate.WaitAsync(0)) return;
        try
        {
            if (_closing) return;
            // Share the dispatcher with Task API state changes. ContextManager samples
            // the foreground window in the background without moving timer mutations there.
            action?.Invoke();
            var context = await _agent.GetContextAsync();
            var evaluation = await _agent.EvaluateFocusAsync(context);
            if (_closing) return;
            _context = context;
            _evaluation = evaluation;
            _selectedWorkspace = _agent.GetActiveWorkspace() ?? Workspaces.FirstOrDefault();
            _errorMessage = null;
            OnPropertyChanged(string.Empty);
        }
        catch (Exception ex)
        {
            _errorMessage = $"Unable to update desktop state: {ex.Message}";
            _evaluation = new() { Reason = "Desktop state is unavailable. Retrying on the next refresh." };
            OnPropertyChanged(string.Empty);
        }
        finally
        {
            if (action is not null) _busy = false;
            _serviceGate.Release();
            NotifyCommands();
        }
    }

    private async Task RunInterventionAsync(InterventionAction action)
    {
        var id = Intervention.Id;
        if (id is null || _closing) return;
        _busy = true;
        NotifyCommands();
        await _serviceGate.WaitAsync();
        try
        {
            if (!_closing) await _agent.HandleInterventionAsync(new(id.Value, action));
            _interventionError = null;
        }
        catch (Exception ex) { _interventionError = ex.Message; }
        finally
        {
            _busy = false;
            _serviceGate.Release();
            OnPropertyChanged(string.Empty);
            NotifyCommands();
        }
    }

    public async Task ShutdownAsync()
    {
        _closing = true;
        _messageCancellation.Cancel();
        NotifyCommands();
        await _serviceGate.WaitAsync();
        try { _agent.StopFocus(); }
        finally { _serviceGate.Release(); }
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanInteract));
        ReturnToWorkCommand.NotifyCanExecuteChanged();
        TemporaryAllowCommand.NotifyCanExecuteChanged();
        StartFocusCommand.NotifyCanExecuteChanged();
        EndTaskCommand.NotifyCanExecuteChanged();
        PauseFocusCommand.NotifyCanExecuteChanged();
        ResumeFocusCommand.NotifyCanExecuteChanged();
        StopFocusCommand.NotifyCanExecuteChanged();
        ChangeWorkspaceCommand.NotifyCanExecuteChanged();
    }

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
