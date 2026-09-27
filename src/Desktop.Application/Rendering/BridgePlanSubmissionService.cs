using System.Text.Json;
using Desktop.Contracts.Bridge;

namespace Desktop.Application.Rendering;

/// <summary>The adapter reports whether submission could have reached the execution side.</summary>
public enum BridgePlanDelivery
{
    Uncertain = 0,
    NotSent,
    ResponseReceived
}

public sealed record BridgePlanDeliveryResult(BridgePlanDelivery Delivery, string? ResponseJson = null)
{
    public override string ToString() => $"{nameof(BridgePlanDeliveryResult)} {{ Delivery = {Delivery} }}";
}

/// <summary>
/// Adapter port for a validated, immutable wire request. Implementations must bound response bytes,
/// sanitize transport failures to a delivery result, and never retry a possibly delivered plan.
/// It conveys acceptance only; checkpoint/output verification owns render completion.
/// </summary>
public interface IBridgePlanSubmissionPort
{
    Task<BridgePlanDeliveryResult> SubmitAsync(string requestJson, CancellationToken cancellationToken);
}

public sealed class BridgePlanSubmissionService(
    BridgePlanSubmissionValidator submissions,
    BridgeWireValidator wire,
    IBridgePlanSubmissionPort port)
{
    public async Task<BridgeResult<BridgeResponseBodyV1>> SubmitAsync(
        string requestJson, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = submissions.Validate(requestJson);
        if (!validation.IsValid)
        {
            return new(null, validation.Errors[0]);
        }
        using var request = JsonDocument.Parse(requestJson);
        var body = request.RootElement.GetProperty("body");
        cancellationToken.ThrowIfCancellationRequested();

        BridgePlanDeliveryResult delivery;
        try
        {
            delivery = await port.SubmitAsync(requestJson, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation of the local wait says nothing about Photoshop side effects.
            return Failure("BRIDGE_EXECUTION_UNKNOWN");
        }

        if (delivery.Delivery == BridgePlanDelivery.NotSent)
        {
            return Failure("BRIDGE_DISCONNECTED");
        }
        if (delivery.Delivery != BridgePlanDelivery.ResponseReceived
            || delivery.ResponseJson is null
            || !wire.Validate(delivery.ResponseJson, BridgeEndpoint.Plans, response: true).IsValid)
        {
            return Failure("BRIDGE_EXECUTION_UNKNOWN");
        }
        BridgeResponseBodyV1 response;
        try
        {
            response = JsonSerializer.Deserialize<BridgeWireEnvelope<BridgeResponseBodyV1>>(
                delivery.ResponseJson, BridgeProtocol.JsonOptions)!.Body;
        }
        catch (JsonException)
        {
            return Failure("BRIDGE_EXECUTION_UNKNOWN");
        }
        if (!string.Equals(response.RequestId, body.GetProperty("request_id").GetString(), StringComparison.Ordinal)
            || !string.Equals(response.CorrelationId, body.GetProperty("correlation_id").GetString(), StringComparison.Ordinal))
        {
            return Failure("BRIDGE_EXECUTION_UNKNOWN");
        }
        // Do not forward arbitrary remote text or details to the application error channel.
        return response.Ok ? BridgeResults.Success(response) : Failure(response.Error!.Code);
    }

    private static BridgeResult<BridgeResponseBodyV1> Failure(string code)
    {
        var error = BridgeWireErrors.Create(code);
        return BridgeResults.Failure<BridgeResponseBodyV1>(error.Code, error.Retryable, error.Scope, error.UserAction);
    }
}
