using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using CluckIn.App.Interfaces;
using CluckIn.App.Models;

namespace CluckIn.App.Services;

public sealed class TaskAnalysisClient(IHttpClientFactory factory) : ITaskAnalysisClient
{
    public async Task<TaskDecision> AnalyzeAsync(TaskAnalyzeRequest request, CancellationToken cancellationToken = default)
    {
        using var client = factory.CreateClient("TaskAnalysis");
        using var response = await client.PostAsJsonAsync("analyze-task", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<TaskDecision>(cancellationToken: cancellationToken);
        if (result is null || result.Decision is not ("allow" or "warn" or "block") ||
            !double.IsFinite(result.Relevance) || result.Relevance is < 0 or > 1 || string.IsNullOrWhiteSpace(result.Reason))
            throw new JsonException("Invalid task analysis response.");
        return result;
    }
}
