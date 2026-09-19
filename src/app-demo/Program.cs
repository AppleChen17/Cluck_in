using System.Text.Json;
using System.Text.Json.Serialization;
using CluckIn.App.Managers;
using CluckIn.App.Services;

var workspaces = new WorkspaceManager();
var timer = new TimerManager();
var context = new ContextManager(new WindowManager(), new BrowserManager(), workspaces, timer);
var agent = new DesktopAgentService(context, workspaces, new FocusManager(), timer);
var json = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
};

if (!args.Contains("--watch", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine(JsonSerializer.Serialize(await agent.GetContextAsync(), json));
    Console.WriteLine("Use --watch for a 25-minute Coding focus session. Ctrl+C exits.");
    return;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
agent.StartFocus("coding", TimeSpan.FromMinutes(25));
Console.WriteLine("Coding focus started. Switch to VS Code, GitHub, or YouTube. Ctrl+C exits.");
try
{
    while (!cancellation.IsCancellationRequested)
    {
        Console.WriteLine(JsonSerializer.Serialize(await agent.GetContextAsync(), json));
        Console.WriteLine(JsonSerializer.Serialize(await agent.EvaluateFocusAsync(), json));
        await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token);
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
finally { agent.StopFocus(); }
