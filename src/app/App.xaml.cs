using System.Windows;
using CluckIn.App.Services;
using CluckIn.App.ViewModels;
using CluckIn.App.Views;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace CluckIn.App;

public partial class App : Application
{
    private WebApplication? _api;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            _api = TaskApiHost.Create(Dispatcher);
            await _api.StartAsync();
            MainWindow = new MainWindow(new MainViewModel(_api.Services.GetRequiredService<DesktopAgentService>()));
            MainWindow.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show($"Unable to start Desktop Agent API: {exception.Message}", "Cluck In");
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_api is not null)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            _api.StopAsync(cancellation.Token).GetAwaiter().GetResult();
            _api.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.OnExit(e);
    }
}
