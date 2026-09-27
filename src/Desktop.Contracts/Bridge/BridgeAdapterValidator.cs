using System.Text;
using System.Text.Json;
using Desktop.Contracts.Validation;

namespace Desktop.Contracts.Bridge;

public sealed class BridgeAdapterValidator(IJsonContractValidator contracts, BridgeWireValidator wire)
{
    public static int ByteLimit(string kind) => kind switch
    {
        "claim_response" => BridgeProtocol.PlanByteLimit + 64 * 1024,
        "event_report" or "event_receipt" or "resource_read" or "resource_chunk"
            or "output_write" or "output_seal" or "output_receipt" => BridgeProtocol.StatusByteLimit,
        _ => BridgeProtocol.MessageByteLimit
    };

    public ContractValidationResult Validate(string json, string expectedKind)
    {
        if (Encoding.UTF8.GetByteCount(json) > ByteLimit(expectedKind)) return Reject("BRIDGE_REQUEST_TOO_LARGE");
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (Duplicate(root) || !contracts.Validate(ContractDocumentKind.AdapterTransport, json).IsValid
                || root.GetProperty("kind").GetString() != expectedKind) return Reject();
            var body = root.GetProperty("body");
            if (expectedKind is "challenge_request" or "challenge_response")
            {
                if (!body.GetProperty("capabilities").EnumerateArray().Any(x => x.GetString() == BridgeAdapterProtocol.Capability))
                    return Reject();
                if (expectedKind == "challenge_response")
                {
                    var issued = body.GetProperty("issued_at_unix_seconds").GetInt64();
                    var expires = body.GetProperty("expires_at_unix_seconds").GetInt64();
                    if (expires <= issued || expires - issued > 30) return Reject();
                }
            }
            if (expectedKind == "event_report"
                && !wire.Validate(body.GetProperty("event").GetRawText(), BridgeEndpoint.Event).IsValid) return Reject();
            if (expectedKind == "claim_response" && body.GetProperty("has_work").GetBoolean())
            {
                var claim = body.GetProperty("claim");
                if (claim.TryGetProperty("plan", out var plan))
                {
                    if (!wire.Validate(plan.GetRawText(), BridgeEndpoint.Plans).IsValid
                        || !string.Equals(claim.GetProperty("plan_digest").GetString(),
                            plan.GetProperty("body").GetProperty("payload").GetProperty("plan_digest").GetString(),
                            StringComparison.OrdinalIgnoreCase)) return Reject();
                }
                else if (claim.GetProperty("state").GetString() == "claimed") return Reject();
            }
            return ContractValidationResult.Success;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            return Reject();
        }
    }

    private static bool Duplicate(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count()
            != value.EnumerateObject().Count() || value.EnumerateObject().Any(x => Duplicate(x.Value)),
        JsonValueKind.Array => value.EnumerateArray().Any(Duplicate),
        _ => false
    };

    private static ContractValidationResult Reject(string code = "BRIDGE_PROTOCOL_MISMATCH")
    {
        var error = BridgeWireErrors.Create(code);
        return ContractValidationResult.Failure(new(error.Code, error.Retryable, error.Scope, error.UserAction));
    }
}
