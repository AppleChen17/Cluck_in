using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CluckIn.App.ViewModels;

static class WpfLaunchChecks
{
    // Opt-in interactive integration check: opens only test-owned windows, then closes them.
    public static Task RunAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var app = new CluckIn.App.App();
            app.InitializeComponent();
            Exception? failure = null;
            app.Startup += (_, _) => app.Dispatcher.InvokeAsync(async () =>
            {
                Window? probe = null;
                try
                {
                    var window = app.MainWindow;
                    var vm = (MainViewModel)window.DataContext;
                    await Task.Delay(300);
                    if (!window.IsVisible) throw new Exception("Main window is not visible.");
                    vm.StartFocusCommand.Execute(null);
                    await Task.Delay(300);
                    probe = new Window { Title = "GitHub - Cluck In test window", Width = 320, Height = 120,
                        Content = new TextBlock { Text = "Temporary foreground detection test", Margin = new Thickness(12) } };
                    probe.Show();
                    // Desktop focus may still be returning from the launch/approval UI.
                    for (var attempt = 0; attempt < 4; attempt++)
                    {
                        probe.Activate();
                        await Task.Delay(1200);
                        if (vm.ActiveWindowTitle == probe.Title) break;
                    }
                    if (vm.ActiveWindowTitle != probe.Title || vm.FocusStatus != "Focused")
                        throw new Exception($"Foreground detection failed: {vm.ActiveWindowTitle} / {vm.FocusStatus}");
                    probe.Title = "YouTube - Cluck In test window";
                    await Task.Delay(1600);
                    if (vm.ActiveWindowTitle != probe.Title || vm.FocusStatus != "Distracted")
                        throw new Exception("Live blocked-title refresh failed.");
                    probe.Title = "Unclassified activity";
                    await Task.Delay(1600);
                    if (vm.FocusStatus != "Neutral") throw new Exception("Live neutral refresh failed.");

                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    var directory = Path.Combine("TestResults", "wpf");
                    Directory.CreateDirectory(directory);
                    using var output = File.Create(Path.Combine(directory, "dashboard.png"));
                    encoder.Save(output);
                    Console.WriteLine("Passed WPF launch, live foreground/title changes, focus mapping and dispatcher responsiveness checks.");
                }
                catch (Exception ex) { failure = ex; }
                finally { probe?.Close(); app.MainWindow.Close(); }
            }, DispatcherPriority.ApplicationIdle);
            app.Run();
            if (failure is null) completion.SetResult();
            else completion.SetException(failure);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
