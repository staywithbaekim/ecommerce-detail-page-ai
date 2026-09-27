using System.Text;
using System.Text.Json;
using Desktop.Contracts.Validation;

namespace Desktop.Contracts.Bridge;

public enum BridgeEndpoint
{
    Handshake,
    Plans,
    Execution,
    Cancel,
    Heartbeat,
    Release,
    Event
}

/// <summary>Strict v1 transport boundary. Authentication and actual resource bytes are checked later.</summary>
public sealed class BridgeWireValidator(IJsonContractValidator contracts)
{
    private static readonly string[] SessionFields =
        ["session_id", "execution_port", "token", "expires_at", "capabilities"];

    public static int ByteLimit(BridgeEndpoint endpoint, bool response = false) => endpoint switch
    {
        BridgeEndpoint.Plans when !response => BridgeProtocol.PlanByteLimit,
        BridgeEndpoint.Execution or BridgeEndpoint.Heartbeat => BridgeProtocol.StatusByteLimit,
        _ => BridgeProtocol.MessageByteLimit
    };

    public ContractValidationResult Validate(string json, BridgeEndpoint endpoint, bool response = false)
    {
        if (!Enum.IsDefined(endpoint) || string.IsNullOrWhiteSpace(json)
            || (endpoint == BridgeEndpoint.Event && response))
        {
            return Reject("BRIDGE_PROTOCOL_MISMATCH");
        }
        if (Encoding.UTF8.GetByteCount(json) > ByteLimit(endpoint, response))
        {
            return Reject("BRIDGE_REQUEST_TOO_LARGE");
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (HasDuplicateProperties(root)
                || !contracts.Validate(ContractDocumentKind.BridgeEnvelope, json).IsValid)
            {
                return Reject("BRIDGE_PROTOCOL_MISMATCH");
            }
            var expectedKind = endpoint == BridgeEndpoint.Handshake
                ? response ? "handshake_response" : "handshake_request"
                : endpoint == BridgeEndpoint.Event ? "event" : response ? "response" : "request";
            if (root.GetProperty("envelope_kind").GetString() != expectedKind)
            {
                return Reject("BRIDGE_PROTOCOL_MISMATCH");
            }
            var body = root.GetProperty("body");
            if (expectedKind == "request")
            {
                var expectedOperation = endpoint switch
                {
                    BridgeEndpoint.Plans => "submit_plan",
                    BridgeEndpoint.Execution => "get_execution",
                    BridgeEndpoint.Cancel => "cancel_execution",
                    BridgeEndpoint.Heartbeat => "heartbeat",
                    BridgeEndpoint.Release => "release",
                    _ => string.Empty
                };
                if (body.GetProperty("operation").GetString() != expectedOperation
                    || (body.TryGetProperty("execution_id", out _)
                        && endpoint is not (BridgeEndpoint.Execution or BridgeEndpoint.Cancel)))
                {
                    return Reject("BRIDGE_PROTOCOL_MISMATCH");
                }
            }
            if (response && !ValidateResponse(body, endpoint))
            {
                return Reject("BRIDGE_PROTOCOL_MISMATCH");
            }
            if (!ValidateErrors(body, expectedKind))
            {
                return Reject("BRIDGE_PROTOCOL_MISMATCH");
            }
            return ContractValidationResult.Success;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException
            or OverflowException)
        {
            return Reject("BRIDGE_PROTOCOL_MISMATCH");
        }
    }

    private static bool ValidateResponse(JsonElement body, BridgeEndpoint endpoint)
    {
        var ok = body.GetProperty("ok").GetBoolean();
        if (ok && body.TryGetProperty("error", out _)) return false;
        if (endpoint == BridgeEndpoint.Handshake)
        {
            if (!ok && SessionFields
                .Any(name => body.TryGetProperty(name, out _))) return false;
            return !ok || body.GetProperty("expires_at").GetDateTimeOffset()
                > body.GetProperty("server_time").GetDateTimeOffset();
        }
        if (!ok) return !body.TryGetProperty("events", out _);
        if (endpoint is BridgeEndpoint.Plans or BridgeEndpoint.Execution or BridgeEndpoint.Cancel)
        {
            if (!body.TryGetProperty("execution_id", out var executionId)
                || !body.TryGetProperty("execution_status", out _)
                || !body.TryGetProperty("event_seq", out var sequence)) return false;
            if (body.TryGetProperty("events", out var events))
            {
                var previous = 0;
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in events.EnumerateArray())
                {
                    var current = item.GetProperty("event_seq").GetInt32();
                    if (current <= previous || current > sequence.GetInt32()
                        || item.GetProperty("execution_id").GetString() != executionId.GetString()
                        || !ids.Add(item.GetProperty("event_id").GetString()!)) return false;
                    previous = current;
                }
            }
        }
        return true;
    }

    private static bool HasDuplicateProperties(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Select(property => property.Name)
            .Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count()
            || value.EnumerateObject().Any(property => HasDuplicateProperties(property.Value)),
        JsonValueKind.Array => value.EnumerateArray().Any(HasDuplicateProperties),
        _ => false
    };

    private static bool ValidateErrors(JsonElement body, string envelopeKind)
    {
        // Only protocol-declared error locations are ours to inspect. RenderPlan is
        // validated by the Application boundary; an untrusted payload named "error"
        // must never be interpreted as a bridge error or make validation throw.
        if (envelopeKind is "handshake_request" or "request") return true;
        if (body.TryGetProperty("error", out var error) && !ValidateError(error)) return false;
        if (body.TryGetProperty("events", out var events))
        {
            foreach (var item in events.EnumerateArray())
            {
                if (item.TryGetProperty("error", out var eventError) && !ValidateError(eventError)) return false;
            }
        }
        return true;
    }

    private static bool ValidateError(JsonElement error)
    {
        var code = error.GetProperty("code").GetString()!;
        if (!BridgeWireErrors.IsKnown(code)) return false;
        var approved = BridgeWireErrors.Create(code);
        if (error.GetProperty("retryable").GetBoolean() != approved.Retryable
            || error.GetProperty("scope").GetString() != approved.Scope
            || error.GetProperty("user_action").GetString() != approved.UserAction) return false;
        return !error.TryGetProperty("details", out var details)
            || details.EnumerateObject().All(detail =>
                detail.Name is "expected" or "actual"
                && detail.Value.ValueKind == JsonValueKind.String
                && IsProtocolVersion(detail.Value.GetString()!));
    }

    private static bool IsProtocolVersion(string value) =>
        value.StartsWith("ps-bridge/v", StringComparison.Ordinal)
        && value.Length is > 11 and <= 15
        && value[11..].All(char.IsAsciiDigit);

    private static ContractValidationResult Reject(string code)
    {
        var error = BridgeWireErrors.Create(code);
        return ContractValidationResult.Failure(new ContractError(
            error.Code, error.Retryable, error.Scope, error.UserAction));
    }
}
