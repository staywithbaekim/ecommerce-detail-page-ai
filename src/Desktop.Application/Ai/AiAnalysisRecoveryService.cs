namespace Desktop.Application.Ai;

/// <summary>
/// Owns restore attempts and gates for one batch context. The caller owns the
/// scheduler and must finish recovery calls before disposing this service.
/// </summary>
public sealed class AiAnalysisRecoveryService : IDisposable
{
    private readonly string _batchId;
    private readonly AiAnalysisScheduler _scheduler;
    private readonly IAiAnalysisCheckpointPort? _checkpointPort;
    private readonly SemaphoreSlim _restoreGate = new(1, 1);
    private readonly SemaphoreSlim _checkpointGate = new(1, 1);
    private bool _schedulerRestoreAttempted;
    private bool _analysisStateRecoveryAttempted;

    public AiAnalysisRecoveryService(
        string batchId,
        AiAnalysisScheduler scheduler,
        IAiAnalysisCheckpointPort? checkpointPort = null)
    {
        _batchId = batchId;
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _checkpointPort = checkpointPort;
    }

    public string? RestoreError { get; private set; }

    public async Task EnsureSchedulerRestoredAsync(CancellationToken cancellationToken)
    {
        await _restoreGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_schedulerRestoreAttempted)
            {
                return;
            }

            _schedulerRestoreAttempted = true;
            try
            {
                _ = await _scheduler.RestoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                RestoreError = exception.Message;
            }
        }
        finally
        {
            _restoreGate.Release();
        }
    }

    public async Task EnsureAnalysisStateRecoveredAsync(CancellationToken cancellationToken)
    {
        if (_checkpointPort is null)
        {
            return;
        }

        await _checkpointGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_analysisStateRecoveryAttempted)
            {
                return;
            }

            _analysisStateRecoveryAttempted = true;
            try
            {
                var result = await _checkpointPort.RecoverAsync(
                    _batchId,
                    cancellationToken).ConfigureAwait(false);
                if (result.Outcome == AiAnalysisCheckpointOutcome.RecoveryRequired)
                {
                    RestoreError = result.ReasonCode;
                }
            }
            catch (InvalidOperationException exception)
            {
                RestoreError = exception.Message;
            }
        }
        finally
        {
            _checkpointGate.Release();
        }
    }

    public void ThrowIfAnalysisStateUnavailable()
    {
        if (!string.IsNullOrWhiteSpace(RestoreError))
        {
            throw new InvalidOperationException(RestoreError);
        }
    }

    public void Dispose()
    {
        _restoreGate.Dispose();
        _checkpointGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
