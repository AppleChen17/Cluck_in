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

    public MainViewModel(DesktopAgentService agent)
    {
        _agent = agent;
        Workspaces = agent.GetWorkspaces();
        _selectedWorkspace = agent.GetActiveWorkspace() ?? Workspaces.FirstOrDefault();
        if (_selectedWorkspace is not null)
            agent.SetActiveWorkspace(_selectedWorkspace.Id);

        StartFocusCommand = new(_ => RunAsync(() => _agent.StartFocus(SelectedWorkspace!.Id, TimeSpan.FromMinutes(25))),
            _ => CanInteract && SelectedWorkspace is not null && !IsFocusModeEnabled);
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

    public string WorkspaceName => _context.WorkspaceName ?? _selectedWorkspace?.Name ?? "No workspace";
    public string ActiveApplication => Display(_context.ActiveWindow.ProcessName);
    public string ActiveWindowTitle => Display(_context.ActiveWindow.WindowTitle);
    public string BrowserPageTitle => Display(_context.Browser?.PageTitle);
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
            // Foreground window interop is synchronous. Keep it off the dispatcher;
            // serialize all service calls because its managers are intentionally single-loop.
            var result = await Task.Run(async () =>
            {
                action?.Invoke();
                var context = await _agent.GetContextAsync();
                var evaluation = await _agent.EvaluateFocusAsync(context);
                return (context, evaluation);
            });
            if (_closing) return;
            _context = result.context;
            _evaluation = result.evaluation;
            _selectedWorkspace = Workspaces.FirstOrDefault(w => w.Id == _context.WorkspaceId);
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

    public async Task ShutdownAsync()
    {
        _closing = true;
        NotifyCommands();
        await _serviceGate.WaitAsync();
        try { await Task.Run(_agent.StopFocus); }
        finally { _serviceGate.Release(); }
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanInteract));
        StartFocusCommand.NotifyCanExecuteChanged();
        PauseFocusCommand.NotifyCanExecuteChanged();
        ResumeFocusCommand.NotifyCanExecuteChanged();
        StopFocusCommand.NotifyCanExecuteChanged();
        ChangeWorkspaceCommand.NotifyCanExecuteChanged();
    }

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
