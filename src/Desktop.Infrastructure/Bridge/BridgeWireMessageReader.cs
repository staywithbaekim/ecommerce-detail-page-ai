using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Desktop.Contracts.Bridge;
using Desktop.Contracts.Validation;

namespace Desktop.Infrastructure.Bridge;

public sealed record BridgeWireReadResult(string? Json, ContractError? Error)
{
    public bool IsSuccess => Error is null;
    public override string ToString() => $"{nameof(BridgeWireReadResult)} {{ Code = {Error?.Code ?? "OK"} }}";
}

/// <summary>
/// Reads a single framed message without trusting Content-Length or accepting partial JSON.
/// The HTTP owner supplies a deadline, rejects content encodings, and owns stream disposal.
/// A read failure does not prove that an earlier submission was never delivered.
/// </summary>
public sealed class BridgeWireMessageReader(BridgeWireValidator wire)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<BridgeWireReadResult> ReadAsync(
        Stream stream, BridgeEndpoint endpoint, bool response, long? declaredLength,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(endpoint) || (endpoint == BridgeEndpoint.Event && response) || declaredLength < 0)
            return Failure("BRIDGE_PROTOCOL_MISMATCH");
        var limit = BridgeWireValidator.ByteLimit(endpoint, response);
        var readResult = await ReadRawAsync(stream, limit, declaredLength, cancellationToken).ConfigureAwait(false);
        if (!readResult.IsSuccess) return readResult;
        var validation = wire.Validate(readResult.Json!, endpoint, response);
        return validation.IsValid ? readResult : new(null, validation.Errors[0]);
    }

    public static async Task<BridgeWireReadResult> ReadRawAsync(
        Stream stream, int limit, long? declaredLength, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit < 1 || limit > BridgeProtocol.PlanByteLimit || declaredLength < 0)
            return Failure("BRIDGE_PROTOCOL_MISMATCH");
        if (declaredLength > limit) return Failure("BRIDGE_REQUEST_TOO_LARGE");

        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        using var bytes = new MemoryStream();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Read at most one byte beyond the limit, including with chunked transfer.
                var count = (int)Math.Min(buffer.Length, limit - bytes.Length + 1);
                var read = await stream.ReadAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (bytes.Length + read > limit) return Failure("BRIDGE_REQUEST_TOO_LARGE");
                bytes.Write(buffer, 0, read);
            }
            if (declaredLength.HasValue && bytes.Length != declaredLength.Value)
                return Failure("BRIDGE_PROTOCOL_MISMATCH");
            var json = StrictUtf8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
            return new(json, null);
        }
        catch (DecoderFallbackException)
        {
            return Failure("BRIDGE_PROTOCOL_MISMATCH");
        }
        catch (IOException)
        {
            return Failure("BRIDGE_DISCONNECTED");
        }
        finally
        {
            // Buffers can contain tokens and handles; never leave them in the shared pool.
            CryptographicOperations.ZeroMemory(bytes.GetBuffer().AsSpan(0, (int)bytes.Length));
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static BridgeWireReadResult Failure(string code)
    {
        var error = BridgeWireErrors.Create(code);
        return new(null, new ContractError(error.Code, error.Retryable, error.Scope, error.UserAction));
    }
}
