using System.Text.Json;

namespace Desktop.Application.Rendering;

/// <summary>Frozen provenance, not an execution id or a product variant.</summary>
public sealed record RenderInstanceIdentity(int Version, string BatchId, string StyleId,
    string PageInstanceId, string SourcePageId, string SnapshotId, string RevisionId,
    string RuleDigest, string InputFactsDigest, string ProjectionDigest);

public static class RenderPlanInstanceIdentity
{
    public const string Capability = "template/page-instance-identity-v1";

    public static RenderInstanceIdentity? Read(JsonElement plan)
    {
        if (!plan.GetProperty("document").TryGetProperty("instance_identity", out var instance)) return null;
        return new(1, plan.GetProperty("batch_id").GetString()!, plan.GetProperty("product_id").GetString()!,
            instance.GetProperty("page_instance_id").GetString()!, instance.GetProperty("source_page_id").GetString()!,
            instance.GetProperty("snapshot_id").GetString()!, plan.GetProperty("template_revision_id").GetString()!,
            instance.GetProperty("rule_digest").GetString()!, instance.GetProperty("input_facts_digest").GetString()!,
            instance.GetProperty("projection_digest").GetString()!);
    }

    public static bool IsValid(RenderInstanceIdentity identity) => identity.Version == 1
        && Id(identity.BatchId) && Id(identity.StyleId) && Id(identity.SourcePageId) && Id(identity.SnapshotId)
        && Id(identity.RevisionId) && identity.PageInstanceId is { Length: 73 }
        && identity.PageInstanceId.StartsWith("instance_", StringComparison.Ordinal)
        && identity.PageInstanceId[9..].All(char.IsAsciiHexDigit)
        && Digest(identity.RuleDigest) && Digest(identity.InputFactsDigest) && Digest(identity.ProjectionDigest);

    /// <summary>Schema validates shape; this validates capability symmetry and reference closure.</summary>
    public static bool MatchesPlan(JsonElement plan)
    {
        var document = plan.GetProperty("document");
        var present = document.TryGetProperty("instance_identity", out var identity);
        if (present != plan.GetProperty("required_capabilities").EnumerateArray().Any(item => item.GetString() == Capability)) return false;
        if (!present) return true;
        if (document.TryGetProperty("output_variant_id", out _) || !IsValid(Read(plan)!)
            || identity.GetProperty("source_page_id").GetString() != document.GetProperty("page_id").GetString()) return false;
        var slots = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var slot in identity.GetProperty("slots").EnumerateArray())
            if (!slots.TryAdd(slot.GetProperty("slot_id").GetString()!, slot.GetProperty("variant_id").GetString()!)) return false;
        var frames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var frame in identity.GetProperty("frames").EnumerateArray())
        {
            if (!slots.TryGetValue(frame.GetProperty("slot_id").GetString()!, out var variant)
                || variant != frame.GetProperty("variant_id").GetString()
                || !frame.TryGetProperty("asset_id", out var asset)
                || !frames.TryAdd(frame.GetProperty("frame_id").GetString()!, asset.GetString()!)) return false;
        }
        if (document.GetProperty("content_type").GetString() == "dynamic_size_table") return frames.Count == 0;
        var targets = plan.GetProperty("targets").EnumerateArray().ToArray();
        return frames.Count == targets.Length && targets.All(target =>
            frames.TryGetValue(target.GetProperty("frame_id").GetString()!, out var asset)
            && asset == target.GetProperty("asset_resource_id").GetString());
    }

    private static bool Id(string? value) => value is { Length: > 0 and <= 128 }
        && char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':');
    private static bool Digest(string? value) => value is { Length: 71 }
        && value.StartsWith("sha256:", StringComparison.Ordinal) && value[7..].All(char.IsAsciiHexDigit);
}
