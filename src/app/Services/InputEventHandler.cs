using System.Text.Json;
using CluckIn.App.Interfaces;
using CluckIn.App.Models;

namespace CluckIn.App.Services;

// Called on the application dispatcher, sharing the same task/session managers as the API.
public sealed class InputEventHandler(ITaskManager taskManager, IDesktopManager desktopManager)
{
    public Task HandleAsync(InputEventRequest input)
    {
        if (input.Source is not ("logitech" or "web" or "system"))
            throw new ArgumentException("Unsupported input source.");
        if (input.Payload.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Input payload must be an object.");

        switch (input.Type)
        {
            case "SHOW_TASK_SELECTION":
                return desktopManager.OpenUrlAsync("http://localhost:5173/#tasks");
            case "SELECT_TASK":
                if (!input.Payload.TryGetProperty("taskId", out var id) ||
                    id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                    throw new ArgumentException("SELECT_TASK requires payload.taskId.");
                return taskManager.StartTaskAsync(id.GetString()!);
            default:
                throw new ArgumentException($"Unsupported input event: {input.Type}");
        }
    }
}
