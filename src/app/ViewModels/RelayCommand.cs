using System.Windows.Input;

namespace CluckIn.App.ViewModels;

/// <summary>Small async command; the supplied operation owns error presentation.</summary>
public sealed class RelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null) : ICommand
{
    private bool _executing;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_executing && (canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _executing = true;
        NotifyCanExecuteChanged();
        try { await execute(parameter); }
        finally
        {
            _executing = false;
            NotifyCanExecuteChanged();
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
