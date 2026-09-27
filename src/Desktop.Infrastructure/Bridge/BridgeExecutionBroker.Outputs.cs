using Desktop.Contracts.Bridge;
using Desktop.Application.Psd;

namespace Desktop.Infrastructure.Bridge;

public sealed partial class BridgeExecutionBroker
{
    private readonly HashSet<string> _validatingOutputs = new(StringComparer.Ordinal);
    // Desktop-only operation. Adapter events/receipts cannot call or bypass this gate.
    public async Task<RenderMediaEvidence> ValidateOutputAsync(string executionId, string templateRelativePath,
        IPsdInspectionPort inspector, CancellationToken cancellationToken)
    {
        Task<RenderMediaEvidence> validation;
        BridgeOutputReservation output;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            if (!_records.TryGetValue(executionId, out var record) || record.TaskId != taskId
                || record.State != "running" || record.CancelRequested || record.Events[^1].EventType != "page_completed"
                || !_deliveries.TryGetValue(executionId, out var delivery) || delivery.Output is null)
                throw new InvalidDataException("RENDER_OUTPUT_NOT_READY");
            if (_committingOutputs.Contains(executionId) || !_validatingOutputs.Add(executionId)) throw new InvalidDataException("RENDER_OUTPUT_NOT_READY");
            output = delivery.Output;
            validation = output.ValidateMediaAsync(templateRelativePath, inspector, cancellationToken);
        }
        finally { _gate.Release(); }
        try
        {
            // Decoding/worker work must not hold the transport gate: claims and
            // cancellation must continue within the existing authenticated deadlines.
            var evidence = await validation.ConfigureAwait(false);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureReady();
                if (!_records.TryGetValue(executionId, out var current) || current.State != "running"
                    || current.CancelRequested || current.Events[^1].EventType != "page_completed"
                    || !_deliveries.TryGetValue(executionId, out var delivery) || !ReferenceEquals(delivery.Output, output))
                    throw new InvalidDataException("RENDER_OUTPUT_NOT_READY");
                return evidence;
            }
            finally { _gate.Release(); }
        }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { _validatingOutputs.Remove(executionId); }
            finally { _gate.Release(); }
        }
    }

    public async Task<BridgeResult<AdapterOutputReceipt>> WriteOutputAsync(string sessionId, AdapterOutputWrite request,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        BridgeOutputReservation? output = null;
        try
        {
            EnsureReady();
            output = GetOutput(sessionId, request.ExecutionId, request.ClaimId, request.PlanDigest, out var cancelling);
            if (cancelling)
            {
                output.ValidateCancelledWrite(request);
                return Failure<AdapterOutputReceipt>("BRIDGE_CANCEL_PENDING");
            }
            return BridgeResults.Success(await output.WriteAsync(request, cancellationToken).ConfigureAwait(false));
        }
        catch (InvalidDataException)
        {
            LastBindingFault = new("output_write", output?.TransferStage ?? "ownership_or_state",
                _records.TryGetValue(request.ExecutionId, out var record) ? record.State : "missing");
            return Failure<AdapterOutputReceipt>("BRIDGE_RESOURCE_BINDING_INVALID");
        }
        finally { _gate.Release(); }
    }

    public async Task<BridgeResult<AdapterOutputReceipt>> SealOutputAsync(string sessionId, AdapterOutputSeal request,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        BridgeOutputReservation? output = null;
        try
        {
            EnsureReady();
            output = GetOutput(sessionId, request.ExecutionId, request.ClaimId, request.PlanDigest, out var cancelling);
            if (cancelling)
            {
                output.ValidateCancelledSeal(request);
                return Failure<AdapterOutputReceipt>("BRIDGE_CANCEL_PENDING");
            }
            return BridgeResults.Success(await output.SealAsync(request, cancellationToken).ConfigureAwait(false));
        }
        catch (InvalidDataException)
        {
            LastBindingFault = new("output_seal", output?.TransferStage ?? "ownership_or_state",
                _records.TryGetValue(request.ExecutionId, out var record) ? record.State : "missing");
            return Failure<AdapterOutputReceipt>("BRIDGE_RESOURCE_BINDING_INVALID");
        }
        finally { _gate.Release(); }
    }

    private BridgeOutputReservation GetOutput(string sessionId, string executionId, string claimId, string digest, out bool cancelling)
    {
        cancelling = false;
        if (_committingOutputs.Contains(executionId) || !Owns(sessionId, executionId, claimId, out var record, out var delivery)
            || record!.State != "running" || delivery?.Output is null
            || !string.Equals(record.PlanDigest, digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("BRIDGE_RESOURCE_BINDING_INVALID");
        cancelling = record.CancelRequested;
        return delivery.Output;
    }
}
