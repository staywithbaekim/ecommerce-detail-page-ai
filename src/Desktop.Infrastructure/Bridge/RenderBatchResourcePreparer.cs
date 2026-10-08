using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Desktop.Application.Files;
using Desktop.Application.Psd;
using Desktop.Application.Rendering;
using Desktop.Application.Templates;
using Desktop.Domain.Inputs;
using Desktop.Infrastructure.Inputs;

namespace Desktop.Infrastructure.Bridge;

/// <summary>Copies only frozen, declared resources to the output volume; original media is opened read-only.</summary>
public sealed class RenderBatchResourcePreparer(IPathPolicy appPaths, ITemplateResourceRepository templates)
{
    public async Task<PreparedRenderResources> PrepareAsync(
        BatchDraft batch, RenderBatchPage page, IPathPolicy outputPaths, CancellationToken token)
    {
        using var performance = RenderPerformance.Start("resources.prepare", page.PageId);
        using var document = JsonDocument.Parse(page.PlanJson!);
        var plan = document.RootElement;
        if (RenderPlanInstanceIdentity.Read(plan) != page.Instance
            || plan.GetProperty("batch_id").GetString() != batch.BatchId
            || plan.GetProperty("product_id").GetString() != page.StyleId
            || plan.GetProperty("document").GetProperty("page_id").GetString() != page.PageId
            || page.Instance is { } instance && (instance.SnapshotId != batch.TemplateSnapshot.SnapshotId
                || instance.RevisionId != batch.TemplateSnapshot.RevisionId
                || batch.TemplateSnapshot is not Desktop.Domain.Templates.BatchRuleTemplateContext context
                || instance.RuleDigest != context.BusinessRuleDigest)) throw Invalid();
        var resources = new List<BridgeResourceSource>();
        var copies = new List<OwnedRenderResourceFile>();
        var templateEvidence = default(Desktop.Application.Psd.PsdInspectionDocument);
        var temporaryFiles = new RenderTemporaryFileLedger(appPaths, outputPaths, page.PlanJson!);
        var directory = $".app-staging/resources/{plan.GetProperty("plan_digest").GetString()![7..]}/{Guid.NewGuid():N}";
        try
        {
            foreach (var resource in plan.GetProperty("resources").EnumerateArray())
            {
                using var resourcePerformance = RenderPerformance.Start("resources.copy_verify");
                token.ThrowIfCancellationRequested();
                var id = resource.GetProperty("resource_id").GetString()!;
                var role = resource.GetProperty("role").GetString()!;
                var media = resource.GetProperty("media_type").GetString()!;
                string source;
                string extension;
                switch (role)
                {
                    case "template_copy":
                        var template = await templates.GetByResourceIdAsync(id, token).ConfigureAwait(false)
                            ?? throw Invalid();
                        if (!batch.TemplateSnapshot.Pages.Any(item => item.PageId == page.PageId && item.TemplateResourceId == id)
                            || plan.GetProperty("template_revision_id").GetString() != batch.TemplateSnapshot.RevisionId) throw Invalid();
                        ValidateTemplateEvidence(page.PlanJson!, template);
                        templateEvidence = template.Inspection;
                        source = appPaths.ResolveOwnedPath(template.InternalRelativePath);
                        extension = ".psd";
                        break;
                    case "high_resolution_render_source":
                        var asset = batch.Assets.SingleOrDefault(item => item.AssetId == id && item.StyleId == page.StyleId)
                            ?? throw Invalid();
                        if (asset.Readability != AssetReadability.Readable || asset.MimeType != media
                            || asset.Length != resource.GetProperty("byte_length").GetInt64()
                            || !string.Equals(asset.Sha256, resource.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)) throw Invalid();
                        source = InputPathGuard.ResolveApprovedFile(batch.InputRoot, asset.SourceRelativePath);
                        extension = media switch { "image/jpeg" => ".jpg", "image/png" => ".png", "image/tiff" => ".tif", _ => throw Invalid() };
                        break;
                    case "deterministic_size_table":
                        if (media != "image/png" || plan.GetProperty("size_table").GetProperty("render_resource_id").GetString() != id) throw Invalid();
                        // This is the deterministic rasterizer's owned resource address, never a plan-supplied path.
                        source = appPaths.ResolveOwnedPath($"batches/{batch.BatchId}/size-tables/{page.StyleId}/{page.PageId}/resources/{id}.png");
                        extension = ".png";
                        break;
                    default: throw Invalid();
                }
                var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + extension;
                var relative = directory + "/" + name;
                copies.Add(await CopyVerifiedAsync(source, outputPaths.ResolveOwnedPath(relative), resource.GetProperty("byte_length").GetInt64(),
                    resource.GetProperty("sha256").GetString()!, token,
                    file => temporaryFiles.TrackAsync(relative, file, token)).ConfigureAwait(false));
                resources.Add(new(id, role, media, relative));
            }
            return new(resources, templateEvidence!, copies, temporaryFiles);
        }
        catch
        {
            PreparedRenderResources.Release(copies);
            throw;
        }
    }

    internal static void ValidateTemplateEvidence(string planJson, ImportedTemplateResource resource)
    {
        using var document = JsonDocument.Parse(planJson);
        var plan = document.RootElement;
        var template = plan.GetProperty("resources").EnumerateArray().Single(item =>
            item.GetProperty("role").GetString() == "template_copy");
        var inspection = resource.Inspection;
        if (inspection is null
            || resource.ResourceId != template.GetProperty("resource_id").GetString()
            || resource.Length != template.GetProperty("byte_length").GetInt64()
            || !string.Equals(resource.Sha256, template.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)
            || resource.Length != inspection.SizeBytes
            || !string.Equals(resource.Sha256, inspection.FileSha256, StringComparison.OrdinalIgnoreCase)
            || !SameDigest(resource.StructureSignature, inspection.StructureSignature)
            || !RenderTemplateEvidenceValidator.Matches(planJson, inspection)) throw Invalid();
    }

    private static bool SameDigest(string left, string right)
    {
        static string Bare(string value) => value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? value[7..] : value;
        var leftDigest = Bare(left);
        var rightDigest = Bare(right);
        return leftDigest.Length == 64 && rightDigest.Length == 64
            && leftDigest.All(char.IsAsciiHexDigit) && rightDigest.All(char.IsAsciiHexDigit)
            && string.Equals(leftDigest, rightDigest, StringComparison.OrdinalIgnoreCase);
    }

    internal Task ReleaseTerminalAsync(string planJson, IPathPolicy outputPaths,
        BridgeExecutionRecord execution, CancellationToken token) =>
        new RenderTemporaryFileLedger(appPaths, outputPaths, planJson).ReleaseTerminalAsync(execution, token);

    internal static async Task<OwnedRenderResourceFile> CopyVerifiedAsync(string source, string destination, long length, string hash,
        CancellationToken token, Func<OwnedRenderResourceFile, Task>? track = null)
    {
        OwnedRenderResourceFile? owned = null;
        try
        {
            using var input = RenderPageCommitter.Open(source, FileShare.Read);
            if (input.Length != length) throw Invalid();
            var directory = Path.GetDirectoryName(destination)!;
            BridgeOutputReservation.ValidatePath(destination);
            Directory.CreateDirectory(directory);
            BridgeOutputReservation.ValidatePath(destination);
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                BridgeResourceLease.VerifyOpenedPath(output.SafeFileHandle, destination);
                owned = OwnedRenderResourceFile.Capture(destination, output.SafeFileHandle);
                if (track is not null) await track(owned).ConfigureAwait(false);
                await input.CopyToAsync(output, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
                output.Position = 0;
                if (output.Length != length || !Convert.ToHexString(await SHA256.HashDataAsync(output, token).ConfigureAwait(false))
                        .Equals(hash, StringComparison.OrdinalIgnoreCase)) throw Invalid();
            }
            BridgeOutputReservation.ValidatePath(destination);
            return owned;
        }
        catch
        {
            owned?.Dispose();
            throw;
        }
    }

    private static InvalidDataException Invalid() => new("RENDER_RESOURCE_PREPARATION_FAILED");
}
