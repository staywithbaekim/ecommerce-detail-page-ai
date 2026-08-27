using System.Collections.Concurrent;
using Desktop.Application.Ai;
using Desktop.Contracts.Ai;

namespace Desktop.Infrastructure.Ai;

public sealed class MockAiAnalyzer : IAiAnalyzer
{
    public Task<AiAnalysisResult> AnalyzeAsync(
        AiAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = request.RequiredSemantics
            .Zip(request.LogicalAssetIds, (semantic, assetId) => new AiCandidate(assetId, semantic, 1m))
            .ToArray();
        return Task.FromResult(new AiAnalysisResult(
            request.RequestId,
            candidates,
            $"usage-{Guid.NewGuid():N}"));
    }
}

public sealed class NullAiCredentialSource : IAiCredentialSource
{
    public ValueTask<string?> GetCredentialAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<string?>(null);
    }
}

public sealed class InMemoryAiUsageRecorder : IAiUsageRecorder
{
    private readonly ConcurrentQueue<AiUsageEvent> _events = new();

    public IReadOnlyCollection<AiUsageEvent> Events => _events.ToArray();

    public Task RecordAsync(AiUsageEvent usageEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Enqueue(usageEvent);
        return Task.CompletedTask;
    }
}
