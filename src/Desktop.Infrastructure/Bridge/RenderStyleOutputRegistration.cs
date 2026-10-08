using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Desktop.Application.Rendering;
using Desktop.Contracts.Validation;
using Desktop.Domain.Inputs;

namespace Desktop.Infrastructure.Bridge;

internal sealed record RenderStyleOutputPage(string PageId, string? OutputVariantId, int Attempt,
    string? PlanDigest, string? JpegName, string? PsdName,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] RenderInstanceIdentity? Instance = null);
internal sealed record RegisteredRenderStyleOutput(RenderStyleOutputTarget Target,
    RenderOutputDirectoryIdentity SourceIdentity, RenderOutputDirectoryIdentity ParentIdentity,
    ImmutableArray<RenderStyleOutputPage> Pages);
internal sealed record RenderStyleOutputRegistration(int Version, string BatchId, string InputManifestDigest,
    string EvidenceRoot, ImmutableArray<RegisteredRenderStyleOutput> Styles);
internal sealed record RenderStyleOutputStage(string BatchId, string StyleId, RenderOutputDirectoryIdentity Identity);

/// <summary>Strict, local publication facts. Not a renderer/wire contract or permission to render.</summary>
internal static class RenderStyleOutputRegistrationData
{
    internal const int MaxBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, MaxDepth = 16
    };
    private static readonly RenderPlanValidator Plans = new(new JsonSchemaContractValidator());

    internal static RenderStyleOutputRegistration Capture(BatchDraft batch, RenderBatchJournal initial,
        RenderStyleOutputPreflight preflight) => Build(batch, initial, preflight.Targets.Select(target =>
            new RegisteredRenderStyleOutput(target, preflight.GetIdentity(target.SourceDirectory),
                preflight.GetIdentity(target.ParentDirectory), [])).ToImmutableArray());

    private static RenderStyleOutputRegistration Build(BatchDraft batch, RenderBatchJournal initial,
        ImmutableArray<RegisteredRenderStyleOutput> identities)
    {
        RenderBatchJournalValidator.Validate(initial, Plans);
        RenderBatchJournalValidator.ValidateUpdate(null, initial);
        if (initial.BatchId != batch.BatchId || batch.OutputSelection is null || batch.ManifestDigest is null
            || identities.IsDefault || identities.Length != batch.Styles.Length
            || initial.Pages.Any(page => !batch.Styles.Any(style => style.StyleId == page.StyleId))) throw Invalid();
        var styles = ImmutableArray.CreateBuilder<RegisteredRenderStyleOutput>();
        foreach (var target in RenderStyleOutputPlanner.Plan(batch))
        {
            var matching = identities.Where(item => item?.Target?.StyleId == target.StyleId).ToArray();
            if (matching.Length != 1) throw Invalid();
            var identity = matching[0];
            ValidateIdentity(identity.SourceIdentity); ValidateIdentity(identity.ParentIdentity);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pages = initial.Pages.Where(page => page.StyleId == target.StyleId).Select(page =>
            {
                if (page.PlanJson is null) return new RenderStyleOutputPage(page.PageId, page.OutputVariantId, page.Attempt, null, null, null, page.Instance);
                using var json = JsonDocument.Parse(page.PlanJson);
                var plan = json.RootElement;
                var output = plan.GetProperty("output");
                var jpg = output.GetProperty("jpg").GetProperty("file_name").GetString()!;
                var psd = output.GetProperty("editable_psd").GetProperty("enabled").GetBoolean()
                    ? output.GetProperty("editable_psd").GetProperty("file_name").GetString() : null;
                foreach (var name in psd is null ? new[] { jpg } : new[] { jpg, psd })
                {
                    var path = RenderStyleOutputPlanner.LocalDirectoryPath(Path.Combine(target.FinalDirectory, name));
                    if (Path.GetFileName(path) != name || Path.GetDirectoryName(path) != target.FinalDirectory) throw Invalid();
                }
                if (!names.Add(jpg) || psd is not null && !names.Add(psd)
                    || batch.IncludeEditablePsd != (psd is not null)) throw Invalid();
                return new RenderStyleOutputPage(page.PageId, page.OutputVariantId, page.Attempt,
                    plan.GetProperty("plan_digest").GetString(), jpg, psd, page.Instance);
            }).ToImmutableArray();
            if (pages.IsEmpty && initial.Version != 4) throw Invalid();
            styles.Add(new(target, identity.SourceIdentity, identity.ParentIdentity, pages));
        }
        return new(initial.Version == 4 ? 2 : 1, batch.BatchId, batch.ManifestDigest, RenderStyleOutputPlanner.LocalDirectoryPath(batch.OutputRoot), styles.ToImmutable());
    }

    internal static void Validate(RenderStyleOutputRegistration value, BatchDraft batch, RenderBatchJournal initial)
    {
        if (value.Version is not (1 or 2) || value.BatchId != batch.BatchId || value.Styles.IsDefault
            || value.Styles.Any(style => style?.Target is null || style.Pages.IsDefault)) throw Invalid();
        if (Encode(value) != Encode(Build(batch, initial, value.Styles))) throw Invalid();
    }

    internal static void ValidateIdentity(RenderOutputDirectoryIdentity identity)
    {
        if (identity is null || identity.Created <= 0 || identity.High == 0 && identity.Low == 0) throw Invalid();
    }

    internal static string Encode<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, Json);
        if (Encoding.UTF8.GetByteCount(json) is < 1 or > MaxBytes) throw Invalid();
        return json;
    }
    internal static T Decode<T>(string json, string hash)
    {
        if (Encoding.UTF8.GetByteCount(json) is < 1 or > MaxBytes || Hash(json) != hash) throw Invalid();
        try { return JsonSerializer.Deserialize<T>(json, Json) ?? throw Invalid(); }
        catch (JsonException) { throw Invalid(); }
    }
    internal static string Hash(string json) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    internal static InvalidDataException Invalid() => new("RENDER_OUTPUT_REGISTRATION_INVALID");
}
