using System.Security.Cryptography;
using System.Text;
using Desktop.Application.Ai;

namespace Desktop.Infrastructure.Ai;

public sealed class AiAnalysisSchedulerFactory(
    IAiServicePort service,
    IAiAnalysisStore store,
    IAiAnalysisSchedulingStateStore schedulingState,
    IAiAnalysisResultStore results,
    IAiAnalysisCopyPreparer copyPreparer,
    IAiAnalysisCopyLeaseManager copyLeaseManager,
    IAiAnalysisCheckpointPort checkpoints,
    TimeProvider timeProvider,
    IConsentPort consentPort) : IAiAnalysisSchedulerFactory
{
    public AiAnalysisScheduler Create(string batchId)
    {
        if (string.IsNullOrWhiteSpace(batchId))
        {
            throw new ArgumentException("批次标识不能为空。", nameof(batchId));
        }

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(batchId)))
            .ToLowerInvariant();
        return new AiAnalysisScheduler(
            service,
            store,
            new BoundedAiAnalysisWorkBuffer(AiAnalysisProductionPolicy.QueueV1),
            AiAnalysisProductionPolicy.BudgetV1,
            timeProvider,
            AiAnalysisProductionPolicy.QueueV1,
            schedulingState,
            $"ai-batch-{digest[..24]}",
            results,
            copyPreparer,
            copyLeaseManager,
            checkpoints,
            consentPort);
    }
}
