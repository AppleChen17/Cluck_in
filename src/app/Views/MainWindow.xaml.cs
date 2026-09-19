using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CluckIn.App.ViewModels;

namespace CluckIn.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _closing;
    private bool _shutdownComplete;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        _refreshTimer.Tick += RefreshTick;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshAsync();
        if (!_closing) _refreshTimer.Start();
    }

    private async void RefreshTick(object? sender, EventArgs e) => await _viewModel.RefreshAsync();

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= RefreshTick;
        try { await _viewModel.ShutdownAsync(); }
        finally
        {
            _shutdownComplete = true;
            Close();
        }
        // TODO: Optional tray hosting can replace close-to-exit in a future version.
    }
}
