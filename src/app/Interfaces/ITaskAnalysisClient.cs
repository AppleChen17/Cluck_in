using CluckIn.App.Models;

namespace CluckIn.App.Interfaces;

public interface ITaskAnalysisClient
{
    Task<TaskDecision> AnalyzeAsync(TaskAnalyzeRequest request, CancellationToken cancellationToken = default);
}
