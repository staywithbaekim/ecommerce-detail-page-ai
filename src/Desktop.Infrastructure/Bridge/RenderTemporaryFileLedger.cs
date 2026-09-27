using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Desktop.Application.Files;
using Desktop.Application.Rendering;
using Desktop.Infrastructure.Files;

namespace Desktop.Infrastructure.Bridge;

/// <summary>Private cleanup evidence, never a source of execution authority or a directory-wide deletion list.</summary>
internal sealed class RenderTemporaryFileLedger
{
    private sealed record Entry(string RelativePath, OwnedRenderResourceFile.Identity Identity);
    private sealed record Inventory(int Version, string BatchId, string PageId, string PlanDigest,
        string IdempotencyKey, bool Released, IReadOnlyList<Entry> Files);
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, MaxDepth = 8
    };
    private readonly IPathPolicy _appPaths, _outputPaths;
    private readonly string _relativePath, _resourcePrefix, _outputPrefix;
    private readonly HashSet<string> _resourceNames;
    private readonly HashSet<string> _outputNames;
    private Inventory _inventory;

    internal RenderTemporaryFileLedger(IPathPolicy appPaths, IPathPolicy outputPaths, string planJson)
    {
        _appPaths = appPaths; _outputPaths = outputPaths;
        using var parsed = JsonDocument.Parse(planJson);
        var plan = parsed.RootElement;
        var digest = plan.GetProperty("plan_digest").GetString()!;
        if (!Regex.IsMatch(digest, "^sha256:[a-f0-9]{64}$")) throw Invalid();
        var batch = plan.GetProperty("batch_id").GetString()!;
        var page = plan.GetProperty("document").GetProperty("page_id").GetString()!;
        _inventory = new(1, batch, page, digest, plan.GetProperty("idempotency_key").GetString()!, false, []);
        _relativePath = $"render/temporary-files/{digest[7..]}.json";
        _resourcePrefix = $".app-staging/resources/{digest[7..]}/";
        var outputDirectory = "rendered/id-" + Hash(batch)[..32];
        _outputPrefix = RenderPageCommitter.StagingDirectory(outputDirectory, batch,
            plan.GetProperty("product_id").GetString()!, page, digest).Replace('\\', '/') + "/";
        _resourceNames = plan.GetProperty("resources").EnumerateArray().Select(resource =>
            Hash(resource.GetProperty("resource_id").GetString()!) + Extension(resource.GetProperty("media_type").GetString()!))
            .ToHashSet(StringComparer.Ordinal);
        _outputNames = ["jpg.part"];
        if (plan.GetProperty("output").GetProperty("editable_psd").GetProperty("enabled").GetBoolean())
            _outputNames.Add("editable_psd.part");
    }

    // Called while the newly created file is still exclusively owned, before writing media bytes or submitting work.
    internal async Task TrackAsync(string relativePath, OwnedRenderResourceFile file, CancellationToken token)
    {
        relativePath = relativePath.Replace('\\', '/');
        if (!Allowed(relativePath) || _inventory.Released || _inventory.Files.Any(item => item.RelativePath == relativePath)) throw Invalid();
        if (_inventory.Files.Count == 0 && File.Exists(_appPaths.ResolveOwnedPath(_relativePath))) throw Invalid();
        _inventory = _inventory with { Files = [.. _inventory.Files, new(relativePath, file.FileIdentity)] };
        await SaveAsync(token).ConfigureAwait(false);
    }

    // The caller has already reconciled the journal, released leases and (for completed pages) verified the commit.
    internal async Task ReleaseTerminalAsync(BridgeExecutionRecord execution, CancellationToken token)
    {
        if (execution.State is not ("completed" or "failed" or "cancelled")
            || execution.TaskId != _inventory.BatchId || execution.PageId != _inventory.PageId
            || execution.PlanDigest != _inventory.PlanDigest || execution.IdempotencyKey != _inventory.IdempotencyKey)
            throw Invalid();
        var path = _appPaths.ResolveOwnedPath(_relativePath);
        FileStream stream;
        try { stream = RenderPageCommitter.Open(path, FileShare.Read); }
        catch (FileNotFoundException) { return; } // Legacy executions have no ownership inventory; never infer one.
        catch (DirectoryNotFoundException) { return; }
        Inventory saved;
        using (stream)
        {
            if (stream.Length is < 1 or > 1048576) throw Invalid();
            try { saved = JsonSerializer.Deserialize<Inventory>(stream, Json) ?? throw Invalid(); }
            catch (JsonException) { throw Invalid(); }
        }
        if (saved.Version != 1 || saved.BatchId != _inventory.BatchId || saved.PageId != _inventory.PageId
            || saved.PlanDigest != _inventory.PlanDigest || saved.IdempotencyKey != _inventory.IdempotencyKey
            || saved.Files.Count > _resourceNames.Count + _outputNames.Count
            || saved.Files.Any(item => item is null)
            || saved.Files.Select(item => item.RelativePath).Distinct(StringComparer.Ordinal).Count() != saved.Files.Count
            || saved.Files.Any(item => !Allowed(item.RelativePath) || item.Identity.Created <= 0
                || item.Identity.High == 0 && item.Identity.Low == 0)) throw Invalid();
        _inventory = saved;
        if (saved.Released) return;
        // No enumeration, recursion, formal output paths, source paths, or age-based decisions.
        foreach (var file in saved.Files)
        {
            token.ThrowIfCancellationRequested();
            OwnedRenderResourceFile.Restore(_outputPaths.ResolveOwnedPath(file.RelativePath), file.Identity).Dispose();
        }
        _inventory = saved with { Released = true };
        await SaveAsync(token).ConfigureAwait(false);
    }

    private bool Allowed(string path)
    {
        if (path.StartsWith(_outputPrefix, StringComparison.Ordinal)) return _outputNames.Contains(path[_outputPrefix.Length..]);
        if (!path.StartsWith(_resourcePrefix, StringComparison.Ordinal)) return false;
        var suffix = path[_resourcePrefix.Length..];
        return suffix.Length > 33 && suffix[32] == '/' && Regex.IsMatch(suffix[..32], "^[a-f0-9]{32}$")
            && _resourceNames.Contains(suffix[33..]);
    }

    private async Task SaveAsync(CancellationToken token)
    {
        var path = _appPaths.ResolveOwnedPath(_relativePath);
        BridgeOutputReservation.ValidatePath(path);
        await new AtomicFileStore(_appPaths).WriteAtomicallyAsync(_relativePath,
            JsonSerializer.SerializeToUtf8Bytes(_inventory, Json), token).ConfigureAwait(false);
        BridgeOutputReservation.ValidatePath(path);
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Extension(string media) => media switch
    {
        "image/vnd.adobe.photoshop" => ".psd", "image/jpeg" => ".jpg", "image/png" => ".png", "image/tiff" => ".tif",
        _ => throw Invalid()
    };
    private static InvalidDataException Invalid() => new("RENDER_TEMPORARY_FILES_INVALID");
}
