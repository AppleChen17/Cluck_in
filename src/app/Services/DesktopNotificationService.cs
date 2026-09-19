using System.Windows;
using System.Windows.Controls;
using CluckIn.App.Interfaces;
using CluckIn.App.Models;

namespace CluckIn.App.Services;

public sealed class DesktopNotificationService : IUserNotificationService
{
    private static async Task ShowAsync(string title, string text, CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current.Dispatcher;
        await dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = new Window {
                Title = title, Width = 480, Height = 320,
                Content = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16) }
            };
            // A session ending closes any session-only display already on screen.
            var registration = cancellationToken.Register(() => dispatcher.BeginInvoke(new Action(window.Close)));
            window.Closed += (_, _) => registration.Dispose();
            window.Show();
        }, System.Windows.Threading.DispatcherPriority.Normal, cancellationToken);
    }

    public Task ShowUrgentMessageAsync(ExternalMessageContract message, CancellationToken cancellationToken) =>
        ShowAsync("Cluck In - Urgent message", $"{message.Sender}\n{message.Title}\n\n{message.Content}", cancellationToken);

    public Task ShowUrgentMessageWithSuggestionAsync(ExternalMessageContract message, string suggestion,
        CancellationToken cancellationToken) =>
        ShowAsync("Cluck In - Suggested reply",
            $"{message.Sender}\n{message.Content}\n\nSuggested reply:\n{suggestion}", cancellationToken);

    public Task ShowSummaryAsync(string summary, bool isEmpty, CancellationToken cancellationToken) =>
        ShowAsync(isEmpty ? "Cluck In - No messages" : "Cluck In - Focus summary", summary, cancellationToken);
}
