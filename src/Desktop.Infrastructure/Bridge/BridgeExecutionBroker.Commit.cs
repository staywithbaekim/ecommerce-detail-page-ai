using Desktop.Application.Rendering;
using Desktop.Application.Files;
using Desktop.Contracts.Bridge;

namespace Desktop.Infrastructure.Bridge;

public sealed partial class BridgeExecutionBroker
{
    private readonly HashSet<string> _committingOutputs = new(StringComparer.Ordinal);

    public async Task<RenderPageCommitResult> RecoverOutputAsync(string executionId, string manifestRelativePath,
        IPathPolicy paths, IRenderPageCommitStore store, CancellationToken cancellationToken)
    {
        BridgeExecutionRecord record;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            if (!_records.TryGetValue(executionId, out record!) || record.TaskId != taskId
                || record.State is not ("unknown" or "completed") || record.CancelRequested
                || record.Events[^1].EventType != (record.State == "completed" ? "execution_completed" : "page_completed")
                || !_committingOutputs.Add(executionId)) throw new InvalidDataException("RENDER_OUTPUT_NOT_READY");
        }
        finally { _gate.Release(); }
        try
        {
            var result = await RenderPageCommitter.RecoverAsync(paths, store, manifestRelativePath, cancellationToken, record).ConfigureAwait(false);
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                EnsureReady();
                if (record.State != "completed") await RecordCommitted(executionId).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
            return result;
        }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { _committingOutputs.Remove(executionId); }
            finally { _gate.Release(); }
        }
    }

    private async Task RecordCommitted(string executionId)
    {
        var record = _records[executionId];
        var completed = new BridgeEventV1($"event_{Guid.NewGuid():N}", record.EventSeq + 1, _time.GetUtcNow(),
            "execution_completed", executionId, "completed");
        _records[executionId] = record with { State = "completed", EventSeq = completed.EventSeq, Events = [.. record.Events, completed] };
        await Save(CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<RenderPageCommitResult> CommitOutputAsync(string executionId, int attempt,
        IRenderPageCommitStore store, CancellationToken cancellationToken)
    {
        BridgeOutputReservation output;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            if (!_records.TryGetValue(executionId, out var record) || record.State != "running" || record.CancelRequested
                || record.Events[^1].EventType != "page_completed" || _validatingOutputs.Contains(executionId)
                || !_deliveries.TryGetValue(executionId, out var delivery) || delivery.Output is null
                || !_committingOutputs.Add(executionId)) throw new InvalidDataException("RENDER_OUTPUT_NOT_READY");
            output = delivery.Output;
        }
        finally { _gate.Release(); }
        try
        {
            // Copy/flush/hash and DB I/O run outside the transport gate. A concurrent
            // disconnect may mark this execution unknown, but cannot dispose its commit leases.
            using var prepared = await output.PrepareCommitAsync(taskId, executionId, attempt, cancellationToken).ConfigureAwait(false);
            var result = await prepared.CommitAsync(store, recovered: false, cancellationToken).ConfigureAwait(false);
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                EnsureReady();
                await RecordCommitted(executionId).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
            return result;
        }
        catch
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (!_poisoned && !_disposed)
                {
                    _records[executionId] = _records[executionId] with { State = "unknown" };
                    await Save(CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally { _gate.Release(); }
            throw;
        }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                _committingOutputs.Remove(executionId);
                if (_deliveries.TryGetValue(executionId, out var delivery))
                {
                    await delivery.Resources.DisposeAsync().ConfigureAwait(false);
                    if (!_poisoned && _records[executionId].State == "completed") delivery.Output?.ReleaseAfterTerminal();
                    else delivery.Output?.Dispose();
                }
            }
            finally { _gate.Release(); }
        }
    }
}
