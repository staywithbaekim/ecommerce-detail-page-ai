namespace Desktop.Application.Ai;

/// <summary>
/// Creates one durable scheduler per batch.  A scheduler checkpoint is
/// intentionally batch-scoped, so sharing one state key across batches would
/// merge unrelated recovery graphs.
/// </summary>
public interface IAiAnalysisSchedulerFactory
{
    AiAnalysisScheduler Create(string batchId);
}
