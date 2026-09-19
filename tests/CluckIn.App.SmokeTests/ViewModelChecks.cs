using System.Windows.Threading;
using CluckIn.App.Interfaces;
using CluckIn.App.Managers;
using CluckIn.App.Models;
using CluckIn.App.Services;
using CluckIn.App.ViewModels;

static class ViewModelChecks
{
    public static Task RunAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { await CheckAsync(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static async Task CheckAsync()
    {
        var count = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            count++;
        }
        var windows = new ChangingWindow();
        var workspaces = new WorkspaceManager();
        workspaces.AddWorkspace(new() { Id = "writing", Name = "Writing", AllowedApplications = ["notepad"] });
        var timer = new TimerManager();
        var service = new DesktopAgentService(new ContextManager(windows, new BrowserManager(), workspaces, timer), workspaces, new FocusManager(), timer);
        var vm = new MainViewModel(service);
        var uiThread = Environment.CurrentManagedThreadId;
        var notificationsOnUi = true;
        vm.PropertyChanged += (_, _) => notificationsOnUi &= Environment.CurrentManagedThreadId == uiThread;

        async Task ExecuteAsync(RelayCommand command, object? parameter = null)
        {
            Check(command.CanExecute(parameter), "Command enabled");
            command.Execute(parameter);
            var timeout = DateTime.UtcNow.AddSeconds(5);
            while (!vm.CanInteract && DateTime.UtcNow < timeout) await Task.Delay(10);
            Check(vm.CanInteract && vm.ErrorMessage is null, "Command completed");
        }

        await vm.RefreshAsync();
        Check(vm.FocusStatus == "Neutral" && vm.TimerText == "00:00", "Initial neutral state");
        Check(vm.WorkspaceName == "Coding" && vm.BrowserPageTitle == "GitHub", "Initial workspace and page");
        Check(!vm.PauseFocusCommand.CanExecute(null) && !vm.ResumeFocusCommand.CanExecute(null), "Idle controls disabled");
        await ExecuteAsync(vm.StartFocusCommand);
        Check(vm.FocusStatus == "Focused" && vm.TimerText == "25:00" && vm.IsFocusModeEnabled, "Start session");
        Check(!vm.StartFocusCommand.CanExecute(null), "Cannot accidentally restart running session");
        await ExecuteAsync(vm.PauseFocusCommand);
        var remaining = vm.TimerText;
        await vm.RefreshAsync();
        Check(vm.SessionStatus == "Paused" && vm.TimerText == remaining && vm.IsFocusModeEnabled, "Pause mapping");
        await ExecuteAsync(vm.ResumeFocusCommand);
        Check(vm.SessionStatus == "Running", "Resume mapping");
        windows.Title = "YouTube - Google Chrome";
        await vm.RefreshAsync();
        Check(vm.FocusStatus == "Distracted" && vm.BrowserPageTitle == "YouTube", "Distracted mapping");
        windows.Process = "unknown";
        windows.Title = "Unknown activity";
        await vm.RefreshAsync();
        Check(vm.FocusStatus == "Neutral" && vm.BrowserPageTitle == "—", "Unknown activity and absent browser");
        await ExecuteAsync(vm.ChangeWorkspaceCommand, vm.Workspaces[1]);
        Check(vm.WorkspaceName == "Writing" && service.GetActiveWorkspace()?.Id == "writing", "Workspace committed through service");
        windows.Process = "notepad";
        await vm.RefreshAsync();
        Check(vm.FocusStatus == "Focused", "New workspace rules active");
        await ExecuteAsync(vm.StopFocusCommand);
        Check(!vm.IsFocusModeEnabled && vm.TimerText == "00:00" && vm.FocusStatus == "Neutral", "Stop mapping");

        windows.Delay = TimeSpan.FromMilliseconds(180);
        var calls = windows.Calls;
        var refresh = vm.RefreshAsync();
        await vm.RefreshAsync();
        var heartbeat = false;
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => heartbeat = true);
        Check(heartbeat && !refresh.IsCompleted, "UI dispatcher responsive during pending service read");
        Check(vm.CanInteract, "Polling does not disable workspace selector");
        await refresh;
        Check(windows.Calls == calls + 1, "Overlapping tick skipped and one snapshot per refresh");
        Check(notificationsOnUi, "Property notifications stay on dispatcher");
        refresh = vm.RefreshAsync();
        var queuedCommand = ExecuteAsync(vm.ChangeWorkspaceCommand, vm.Workspaces[0]);
        await Task.WhenAll(refresh, queuedCommand);
        Check(vm.WorkspaceName == "Coding", "User command queued behind refresh is preserved");
        windows.Fail = true;
        await vm.RefreshAsync();
        Check(vm.ErrorMessage is not null && vm.FocusStatus == "Neutral", "Refresh errors visible without crashing");
        windows.Fail = false;
        await vm.RefreshAsync();
        Check(vm.ErrorMessage is null, "Refresh recovers");
        await vm.ShutdownAsync();
        Check(!vm.StartFocusCommand.CanExecute(null), "Commands disabled on shutdown");
        Console.WriteLine($"Passed {count} ViewModel checks.");
    }

    private sealed class ChangingWindow : IWindowManager
    {
        public string Process { get; set; } = "chrome";
        public string Title { get; set; } = "GitHub - Google Chrome";
        public TimeSpan Delay { get; set; }
        public bool Fail { get; set; }
        public int Calls { get; private set; }
        public async Task<ActiveWindowInfo> GetActiveWindowAsync()
        {
            Calls++;
            await Task.Delay(Delay);
            if (Fail) throw new InvalidOperationException("Simulated read failure");
            return new() { ProcessName = Process, WindowTitle = Title };
        }
    }
}
