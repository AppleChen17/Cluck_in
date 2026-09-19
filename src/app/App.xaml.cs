using System.Windows;
using CluckIn.App.Services;
using CluckIn.App.ViewModels;
using CluckIn.App.Views;

namespace CluckIn.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MainWindow = new MainWindow(new MainViewModel(DesktopAgentFactory.Create()));
        MainWindow.Show();
    }
}
