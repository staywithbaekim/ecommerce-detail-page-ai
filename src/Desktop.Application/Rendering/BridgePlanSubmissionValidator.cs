using System.Text.Json;
using Desktop.Contracts.Bridge;
using Desktop.Contracts.Validation;

namespace Desktop.Application.Rendering;

/// <summary>Closes the wire envelope against the renderer-neutral plan, before binding actual files.</summary>
public sealed class BridgePlanSubmissionValidator(
    BridgeWireValidator wire, IRenderPlanValidator plans)
{
    public ContractValidationResult Validate(string json)
    {
        var validation = wire.Validate(json, BridgeEndpoint.Plans);
        if (!validation.IsValid) return validation;
        using var document = JsonDocument.Parse(json);
        var body = document.RootElement.GetProperty("body");
        var payload = body.GetProperty("payload");
        var plan = payload.GetProperty("render_plan");
        if (!plans.Validate(plan.GetRawText()).IsValid) return Reject("BRIDGE_PLAN_INVALID");
        if (!string.Equals(payload.GetProperty("plan_digest").GetString(),
                plan.GetProperty("plan_digest").GetString(), StringComparison.OrdinalIgnoreCase)
            || body.GetProperty("idempotency_key").GetString() != plan.GetProperty("idempotency_key").GetString()
            || body.GetProperty("correlation_id").GetString() != plan.GetProperty("correlation_id").GetString())
        {
            return Reject("BRIDGE_PLAN_INVALID");
        }
        var bindings = payload.GetProperty("resource_bindings").EnumerateArray().ToArray();
        var resources = plan.GetProperty("resources").EnumerateArray().ToArray();
        var boundIds = new HashSet<string>(StringComparer.Ordinal);
        var handles = new HashSet<string>(StringComparer.Ordinal);
        if (bindings.Length != resources.Length) return Reject("BRIDGE_RESOURCE_BINDING_INVALID");
        foreach (var binding in bindings)
        {
            var id = binding.GetProperty("resource_id").GetString()!;
            if (!boundIds.Add(id) || !handles.Add(binding.GetProperty("session_handle").GetString()!))
                return Reject("BRIDGE_RESOURCE_BINDING_INVALID");
            var resource = resources.SingleOrDefault(item => item.GetProperty("resource_id").GetString() == id);
            if (resource.ValueKind == JsonValueKind.Undefined
                || !binding.TryGetProperty("byte_length", out var length)
                || length.GetInt64() != resource.GetProperty("byte_length").GetInt64()
                || binding.GetProperty("purpose").GetString() != resource.GetProperty("role").GetString()
                || !string.Equals(binding.GetProperty("sha256").GetString(),
                    resource.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                return Reject("BRIDGE_RESOURCE_BINDING_INVALID");
        }
        var outputs = payload.GetProperty("output_bindings").EnumerateArray().ToArray();
        var output = plan.GetProperty("output");
        // RenderPlan v1 is one document; a product scope still carries one page per plan.
        if (outputs.Length != 1
            || outputs[0].GetProperty("artifact_set_id").GetString() != output.GetProperty("artifact_set_id").GetString()
            || outputs[0].GetProperty("overwrite_policy").GetString() != output.GetProperty("overwrite_policy").GetString()
            || handles.Contains(outputs[0].GetProperty("output_root_handle").GetString()!))
            return Reject("BRIDGE_RESOURCE_BINDING_INVALID");
        var confirmed = outputs[0].GetProperty("confirmed_target_ids").GetArrayLength();
        var overwrite = output.GetProperty("overwrite_policy").GetString();
        if ((overwrite == "never" && confirmed != 0)
            || (overwrite == "confirmed_targets_only" && confirmed == 0))
            return Reject("BRIDGE_RESOURCE_BINDING_INVALID");
        return ContractValidationResult.Success;
    }

    private static ContractValidationResult Reject(string code)
    {
        var error = BridgeWireErrors.Create(code);
        return ContractValidationResult.Failure(new ContractError(
            error.Code, error.Retryable, error.Scope, error.UserAction));
    }
}
