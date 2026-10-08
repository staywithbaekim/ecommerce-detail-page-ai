using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Buffers;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;

namespace Desktop.Application.Templates;

/// <summary>Canonical versioned E1 serialization and integrity validation.</summary>
public static class TemplateInstanceProjectionSnapshotCodec
{
    public const int CurrentSchemaVersion = 1;
    private const int MaximumJsonBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SearchValues<char> HexCharacters = SearchValues.Create("0123456789abcdef");

    public static TemplateInstanceProjectionSnapshot Freeze(
        TemplateInstanceProjectionRequest request, TemplateInstanceProjectionResult projection)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(projection);
        if (projection.Status == TemplateInstanceProjectionStatus.Rejected || projection.InputFactsDigest is null
            || projection.Instances.IsDefault || !projection.Diagnostics.IsEmpty)
            throw new TemplateRuleViolationException("TPL_INSTANCE_PROJECTION_NOT_FREEZABLE");
        if (projection.Instances.Any(instance => instance.BatchId != request.Scope.BatchId
            || instance.StyleId != request.Scope.StyleId || instance.SnapshotId != request.Scope.SnapshotId
            || instance.RevisionId != request.Scope.RevisionId || instance.RuleDigest != request.Scope.RuleDigest
            || instance.InputFactsDigest != projection.InputFactsDigest))
            throw new TemplateRuleViolationException("TPL_INSTANCE_PROJECTION_BINDING_INVALID");

        var snapshot = new TemplateInstanceProjectionSnapshot(CurrentSchemaVersion, 1,
            request.Scope.BatchId, request.Scope.StyleId, request.Scope.SnapshotId, request.Scope.RevisionId,
            request.Scope.RuleDigest, projection.InputFactsDigest, "", projection.Instances);
        snapshot = snapshot with { ProjectionDigest = ComputeDigest(snapshot) };
        Validate(snapshot);
        return snapshot;
    }

    public static string Serialize(TemplateInstanceProjectionSnapshot snapshot)
    {
        Validate(snapshot);
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
            throw new TemplateRuleViolationException("TPL_INSTANCE_SNAPSHOT_TOO_LARGE");
        return json;
    }

    public static TemplateInstanceProjectionSnapshot Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
            throw new TemplateRuleViolationException("TPL_INSTANCE_SNAPSHOT_TOO_LARGE");
        try
        {
            var snapshot = JsonSerializer.Deserialize<TemplateInstanceProjectionSnapshot>(json, JsonOptions)
                ?? throw new TemplateRuleViolationException("TPL_INSTANCE_SNAPSHOT_INVALID");
            Validate(snapshot);
            return snapshot;
        }
        catch (JsonException)
        {
            throw new TemplateRuleViolationException("TPL_INSTANCE_SNAPSHOT_INVALID");
        }
    }

    public static string CommandDigest(TemplateInstanceProjectionSnapshot snapshot) =>
        Hash(new { schema = "template-instance-freeze-command/v1", snapshot.BatchId, snapshot.StyleId,
            snapshot.ProjectionDigest, snapshot.SnapshotVersion });

    public static void Validate(TemplateInstanceProjectionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SchemaVersion != CurrentSchemaVersion || snapshot.SnapshotVersion != 1
            || snapshot.Instances.IsDefault || snapshot.Instances.Length > TemplateInstanceProjector.MaximumFactEntries)
            throw new TemplateRuleViolationException("TPL_INSTANCE_SNAPSHOT_VERSION_INVALID");
        TemplateValueRules.Identifier(snapshot.BatchId, "TPL_INSTANCE_SNAPSHOT_INVALID");
        TemplateValueRules.Identifier(snapshot.StyleId, "TPL_INSTANCE_SNAPSHOT_INVALID");
        TemplateValueRules.Identifier(snapshot.SnapshotId, "TPL_INSTANCE_SNAPSHOT_INVALID");
        TemplateValueRules.Identifier(snapshot.RevisionId, "TPL_INSTANCE_SNAPSHOT_INVALID");
        TemplateValueRules.Digest(snapshot.RuleDigest, "TPL_INSTANCE_SNAPSHOT_DIGEST_INVALID");
        TemplateValueRules.Digest(snapshot.InputFactsDigest, "TPL_INSTANCE_SNAPSHOT_DIGEST_INVALID");
        TemplateValueRules.Digest(snapshot.ProjectionDigest, "TPL_INSTANCE_SNAPSHOT_DIGEST_INVALID");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var orders = new HashSet<int>();
        foreach (var instance in snapshot.Instances)
        {
            if (instance is null || !ids.Add(instance.PageInstanceId) || !orders.Add(instance.Order)
                || instance.BatchId != snapshot.BatchId || instance.StyleId != snapshot.StyleId
                || instance.SnapshotId != snapshot.SnapshotId || instance.RevisionId != snapshot.RevisionId
                || instance.RuleDigest != snapshot.RuleDigest || instance.InputFactsDigest != snapshot.InputFactsDigest
                || instance.Order < 0 || instance.RepetitionIndex < 0 || instance.Slots.IsDefault || instance.Frames.IsDefault
                || instance.BlockingReasons.IsDefault || !Enum.IsDefined(instance.Status))
                throw new TemplateRuleViolationException("TPL_INSTANCE_SNAPSHOT_BINDING_INVALID");
            if (!instance.PageInstanceId.StartsWith("instance_", StringComparison.Ordinal)
                || instance.PageInstanceId.Length != 73
                || instance.PageInstanceId.AsSpan(9).IndexOfAnyExcept(HexCharacters) >= 0)
                throw new TemplateRuleViolationException("TPL_INSTANCE_SNAPSHOT_BINDING_INVALID");
            if (instance.Slots.Any(slot => slot is null)
                || instance.Frames.Any(frame => frame is null || frame.SelectionRevision < 0)
                || instance.BlockingReasons.Any(reason => reason is null))
                throw new TemplateRuleViolationException("TPL_INSTANCE_SNAPSHOT_BINDING_INVALID");
        }
        if (snapshot.Instances.Length > 0
            && (!orders.SetEquals(Enumerable.Range(0, snapshot.Instances.Length))
                || !snapshot.Instances.Select(instance => instance.Order).SequenceEqual(Enumerable.Range(0, snapshot.Instances.Length))))
            throw new TemplateRuleViolationException("TPL_INSTANCE_SNAPSHOT_ORDER_INVALID");
        if (snapshot.ProjectionDigest != ComputeDigest(snapshot))
            throw new TemplateRuleViolationException("TPL_INSTANCE_SNAPSHOT_DIGEST_INVALID");
    }

    public static TemplateInstanceProjectionSnapshot WithVersion(
        TemplateInstanceProjectionSnapshot snapshot, long version) =>
        snapshot with { SnapshotVersion = version, ProjectionDigest = ComputeDigest(snapshot with { SnapshotVersion = version, ProjectionDigest = "" }) };

    private static string ComputeDigest(TemplateInstanceProjectionSnapshot snapshot) => Hash(new
    {
        schema = "template-instance-projection-snapshot/v1", snapshot.SchemaVersion, snapshot.SnapshotVersion,
        snapshot.BatchId, snapshot.StyleId, snapshot.SnapshotId, snapshot.RevisionId, snapshot.RuleDigest,
        snapshot.InputFactsDigest, snapshot.Instances
    });

    private static string Hash<T>(T value) => "sha256:" + Convert.ToHexStringLower(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions)));
}
