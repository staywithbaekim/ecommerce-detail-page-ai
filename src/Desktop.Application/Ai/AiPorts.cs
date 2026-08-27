using Desktop.Contracts.Ai;

namespace Desktop.Application.Ai;

public interface IAiAnalyzer
{
    Task<AiAnalysisResult> AnalyzeAsync(AiAnalysisRequest request, CancellationToken cancellationToken);
}

public interface IAiCredentialSource
{
    ValueTask<string?> GetCredentialAsync(CancellationToken cancellationToken);
}

public interface IAiUsageRecorder
{
    Task RecordAsync(AiUsageEvent usageEvent, CancellationToken cancellationToken);
}
