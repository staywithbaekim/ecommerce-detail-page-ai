using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Desktop.Contracts.Bridge;
using Desktop.Contracts.Validation;

namespace Desktop.Application.Rendering;

public interface IRenderPlanValidator
{
    ContractValidationResult Validate(string renderPlanJson);
}

public sealed class RenderPlanValidator(IJsonContractValidator validator) : IRenderPlanValidator
{
    public ContractValidationResult Validate(string renderPlanJson)
    {
        var schema = validator.Validate(ContractDocumentKind.RenderPlan, renderPlanJson);
        if (!schema.IsValid)
        {
            return schema;
        }

        try
        {
            using var document = JsonDocument.Parse(renderPlanJson);
            var root = document.RootElement;
            if (root.GetProperty("document").TryGetProperty("output_variant_id", out _)
                != root.GetProperty("required_capabilities").EnumerateArray().Any(value => value.GetString() == RenderPlanVariantIdentity.Capability))
                return Failure("RENDER_PLAN_VARIANT_IDENTITY_INVALID");
            var resources = root.GetProperty("resources").EnumerateArray().ToArray();
            var resourceIds = resources
                .Select(resource => resource.GetProperty("resource_id").GetString()!)
                .ToArray();
            if (resourceIds.Distinct(StringComparer.Ordinal).Count() != resourceIds.Length)
            {
                return Failure("RENDER_PLAN_RESOURCE_DUPLICATE");
            }

            var resourcesById = resources.ToDictionary(
                resource => resource.GetProperty("resource_id").GetString()!,
                resource => resource,
                StringComparer.Ordinal);
            var documentResourceId = root.GetProperty("document")
                .GetProperty("template_resource_id").GetString()!;
            var documentNode = root.GetProperty("document");
            if (!RenderPlanInstanceIdentity.MatchesPlan(root))
                return Failure("RENDER_PLAN_INSTANCE_IDENTITY_INVALID");
            if (!resourcesById.TryGetValue(documentResourceId, out var templateResource)
                || !string.Equals(
                    templateResource.GetProperty("role").GetString(),
                    "template_copy",
                    StringComparison.Ordinal))
            {
                return Failure("RENDER_PLAN_TEMPLATE_RESOURCE_INVALID");
            }

            var usedResourceIds = new HashSet<string>(StringComparer.Ordinal)
            {
                documentResourceId
            };
            var frameIds = new HashSet<string>(StringComparer.Ordinal);
            var layerTargets = new HashSet<string>(StringComparer.Ordinal);
            var canvas = root.GetProperty("document").GetProperty("canvas");
            var canvasWidth = canvas.GetProperty("width").GetInt32();
            var canvasHeight = canvas.GetProperty("height").GetInt32();
            var explicitPlacement = root.GetProperty("required_capabilities").EnumerateArray()
                .Any(item => item.GetString() == RenderPlanPlacementMigration.Capability);
            var calibratedSizeTable = root.GetProperty("required_capabilities").EnumerateArray()
                .Any(item => item.GetString() == RenderPlanPlacementMigration.SizeTableCapability);
            var sizeInsertion = RenderPlanPlacementMigration.HasSizeInsertion(root);
            if (explicitPlacement && !RenderPlanPlacementMigration.IsExplicit(root)) return Failure("RENDER_PLAN_PLACEMENT_INVALID");
            foreach (var target in root.GetProperty("targets").EnumerateArray())
            {
                var frameId = target.GetProperty("frame_id").GetString()!;
                var layerKey = string.Join('/', target.GetProperty("layer_path")
                    .EnumerateArray().Select(segment => segment.GetInt32()));
                if (!frameIds.Add(frameId) || !layerTargets.Add(layerKey))
                {
                    return Failure("RENDER_PLAN_TARGET_DUPLICATE");
                }

                var assetResourceId = target.GetProperty("asset_resource_id").GetString()!;
                if (!resourcesById.TryGetValue(assetResourceId, out var sourceResource)
                    || !string.Equals(
                        sourceResource.GetProperty("role").GetString(),
                        calibratedSizeTable ? "deterministic_size_table" : "high_resolution_render_source",
                        StringComparison.Ordinal))
                {
                    return Failure("RENDER_PLAN_SOURCE_RESOURCE_INVALID");
                }

                usedResourceIds.Add(assetResourceId);
                var safeBounds = target.GetProperty("layout").GetProperty("safe_bounds");
                var left = safeBounds.GetProperty("left").GetDecimal();
                var top = safeBounds.GetProperty("top").GetDecimal();
                var right = safeBounds.GetProperty("right").GetDecimal();
                var bottom = safeBounds.GetProperty("bottom").GetDecimal();
                if (right <= left
                    || bottom <= top
                    || right > canvasWidth
                    || bottom > canvasHeight)
                {
                    return Failure("RENDER_PLAN_SAFE_BOUNDS_INVALID");
                }
                if (target.GetProperty("layout").TryGetProperty("placement", out var placement))
                {
                    var width = placement.GetProperty("width").GetDecimal();
                    var height = placement.GetProperty("height").GetDecimal();
                    var sourceWidth = placement.GetProperty("source_width").GetInt32();
                    var sourceHeight = placement.GetProperty("source_height").GetInt32();
                    var x = placement.GetProperty("left").GetDecimal();
                    var y = placement.GetProperty("top").GetDecimal();
                    // At most one millionth relative error permits deterministic decimal rounding, never distortion.
                    if (!explicitPlacement || Math.Abs(width * sourceHeight - height * sourceWidth)
                            > Math.Max(width * sourceHeight, height * sourceWidth) * 0.000001m
                        || x >= canvasWidth || y >= canvasHeight || x + width <= 0 || y + height <= 0)
                        return Failure("RENDER_PLAN_PLACEMENT_INVALID");
                    // Every cell must remain in the explicitly calibrated region; no table cropping.
                    if (calibratedSizeTable && (x < left || y < top || x + width > right || y + height > bottom))
                        return Failure("RENDER_PLAN_PLACEMENT_INVALID");
                }
            }

            var sizeTable = root.GetProperty("size_table");
            if (sizeInsertion)
            {
                var insertion = sizeTable.GetProperty("insertion");
                var p = insertion.GetProperty("placement"); var b = insertion.GetProperty("safe_bounds");
                var x = p.GetProperty("left").GetDecimal(); var y = p.GetProperty("top").GetDecimal();
                var w = p.GetProperty("width").GetDecimal(); var h = p.GetProperty("height").GetDecimal();
                var sw = p.GetProperty("source_width").GetDecimal(); var sh = p.GetProperty("source_height").GetDecimal();
                if (Math.Abs(w * sh - h * sw) > Math.Max(w * sh, h * sw) * 0.000001m
                    || x < b.GetProperty("left").GetDecimal() || y < b.GetProperty("top").GetDecimal()
                    || x + w > b.GetProperty("right").GetDecimal() || y + h > b.GetProperty("bottom").GetDecimal()
                    || b.GetProperty("right").GetDecimal() > canvasWidth || b.GetProperty("bottom").GetDecimal() > canvasHeight)
                    return Failure("RENDER_PLAN_PLACEMENT_INVALID");
                var hidden = insertion.GetProperty("hidden_layers").EnumerateArray().ToArray();
                if (hidden.Select(item => item.GetProperty("layer_id").GetInt32()).Distinct().Count() != hidden.Length
                    || hidden.Select(item => item.GetProperty("layer_path")[0].GetInt32()).Distinct().Count() != hidden.Length)
                    return Failure("RENDER_PLAN_TARGET_DUPLICATE");
            }
            if (sizeTable.ValueKind != JsonValueKind.Null)
            {
                var renderResourceId = sizeTable.GetProperty("render_resource_id").GetString()!;
                if (!resourcesById.TryGetValue(renderResourceId, out var renderResource)
                    || !string.Equals(
                        renderResource.GetProperty("role").GetString(),
                        "deterministic_size_table",
                        StringComparison.Ordinal))
                {
                    return Failure("RENDER_PLAN_SIZE_RESOURCE_INVALID");
                }

                usedResourceIds.Add(renderResourceId);
                if (calibratedSizeTable && (root.GetProperty("targets")[0].GetProperty("asset_resource_id").GetString() != renderResourceId
                    || renderResource.GetProperty("media_type").GetString() != "image/png"
                    || !string.Equals(sizeTable.GetProperty("grid_digest").GetString(),
                        "sha256:" + renderResource.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)))
                    return Failure("RENDER_PLAN_SIZE_RESOURCE_INVALID");
                if (sizeInsertion && (renderResource.GetProperty("media_type").GetString() != "image/png"
                    || !string.Equals(sizeTable.GetProperty("grid_digest").GetString(),
                        "sha256:" + renderResource.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)))
                    return Failure("RENDER_PLAN_SIZE_RESOURCE_INVALID");
            }

            if (resourcesById.Keys.Any(resourceId => !usedResourceIds.Contains(resourceId)))
            {
                return Failure("RENDER_PLAN_RESOURCE_UNUSED");
            }

            var declaredDigest = root.GetProperty("plan_digest").GetString();
            var computedDigest = RenderPlanDigest.Compute(renderPlanJson);
            if (!string.Equals(declaredDigest, computedDigest, StringComparison.OrdinalIgnoreCase))
            {
                return Failure("RENDER_PLAN_DIGEST_MISMATCH");
            }

            return ContractValidationResult.Success;
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidOperationException
            or FormatException
            or OverflowException)
        {
            return Failure("RENDER_PLAN_SEMANTIC_INVALID");
        }
    }

    private static ContractValidationResult Failure(string code) =>
        ContractValidationResult.Failure(new ContractError(
            code,
            Retryable: false,
            Scope: "render_plan",
            UserAction: "渲染计划未通过资源、结构或摘要校验，请返回生成前检查。"));
}

/// <summary>
/// Stable renderer-neutral digest. Objects are ordered by property name,
/// arrays retain business order, numbers are normalized, and plan_digest is
/// replaced by an all-zero placeholder before hashing. Formatting, machine
/// paths and adapter state therefore cannot change the result.
/// </summary>
public static class RenderPlanDigest
{
    private const string DigestPrefix = "sha256:";
    private static readonly string Placeholder = DigestPrefix + new string('0', 64);

    public static string Compute(string renderPlanJson)
    {
        using var document = JsonDocument.Parse(renderPlanJson);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(writer, document.RootElement, isRoot: true);
        }

        return DigestPrefix + Convert.ToHexString(SHA256.HashData(stream.ToArray()))
            .ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value, bool isRoot = false)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(
                             property => property.Name,
                             StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    if (isRoot && string.Equals(property.Name, "plan_digest", StringComparison.Ordinal))
                    {
                        writer.WriteStringValue(Placeholder);
                    }
                    else
                    {
                        WriteCanonical(writer, property.Value);
                    }
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                WriteCanonicalNumber(writer, value.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException("Unsupported RenderPlan value kind.");
        }
    }

    private static void WriteCanonicalNumber(Utf8JsonWriter writer, string raw)
    {
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            writer.WriteNumberValue(integer);
            return;
        }

        if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            var normalized = number == 0m
                ? "0"
                : number.ToString("G29", CultureInfo.InvariantCulture);
            writer.WriteRawValue(normalized, skipInputValidation: false);
            return;
        }

        throw new JsonException("RenderPlan number is outside the supported deterministic range.");
    }
}

public sealed record PhotoshopAvailability(bool IsAvailable, string? ReasonCode = null);

public interface IPhotoshopAvailabilityPort
{
    Task<PhotoshopAvailability> CheckAsync(CancellationToken cancellationToken);
}

public interface IBridgeClient
{
    Task<BridgeResult<BridgeSession>> HandshakeAsync(
        BridgeHandshakeRequest request,
        CancellationToken cancellationToken);

    Task<BridgeResult<BridgeExecution>> SubmitAsync(
        BridgeExecutionRequest request,
        CancellationToken cancellationToken);

    Task<BridgeResult<BridgeExecution>> QueryAsync(
        string executionId,
        string accessToken,
        CancellationToken cancellationToken);

    Task<bool> HeartbeatAsync(string accessToken, CancellationToken cancellationToken);
}
