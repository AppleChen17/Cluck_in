namespace Loupedeck.CluckInPlugin;

using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public static class IntegrationClient{
    private const string DefaultUrl =
        "http://127.0.0.1:8765/input-event";

    private static readonly HttpClient Client = new HttpClient{
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly JsonSerializerOptions JsonOptions =
        new JsonSerializerOptions{
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

    public static async Task SendAsync(InputEventRequest inputEvent){
        try{
            var url = Environment.GetEnvironmentVariable(
                "CLUCKIN_INPUT_EVENT_URL"
            );

            if(string.IsNullOrWhiteSpace(url)){
                url = inputEvent.Type is "SELECT_TASK" or "SHOW_TASK_SELECTION"
                    ? "http://127.0.0.1:5180/input-event"
                    : DefaultUrl;
            }

            var json = JsonSerializer.Serialize(
                inputEvent,
                JsonOptions
            );

            using var content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json"
            );

            using var response = await Client.PostAsync(
                url,
                content
            );

            response.EnsureSuccessStatusCode();

            PluginLog.Info(
                $"InputEvent sent: type={inputEvent.Type}, status={(int)response.StatusCode}"
            );
        }
        catch(Exception ex){
            PluginLog.Info(
                $"InputEvent unavailable: type={inputEvent.Type}, error={ex.Message}"
            );
        }
    }
}
