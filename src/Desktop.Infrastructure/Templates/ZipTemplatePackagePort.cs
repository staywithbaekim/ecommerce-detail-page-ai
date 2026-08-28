using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Desktop.Application.Templates;
using Desktop.Contracts.Validation;

namespace Desktop.Infrastructure.Templates;

public sealed class ZipTemplatePackagePort(IJsonContractValidator? contracts = null) : ITemplatePackagePort
{
    private const int BufferSize = 64 * 1024;
    private const int MaxEntryCount = 1024;
    private const long MaxManifestLength = 4 * 1024 * 1024;
    private const long MaxEntryLength = 2L * 1024 * 1024 * 1024;
    private const long MaxPackageContentLength = 8L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task ExportAsync(
        TemplatePackageManifest manifest,
        IReadOnlyDictionary<string, string> sourceFiles,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ValidateManifest(manifest);
        ValidateSchema(manifest);
        ArgumentNullException.ThrowIfNull(sourceFiles);
        var temporaryPath = destinationPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             BufferSize,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using (var manifestStream = manifestEntry.Open())
                {
                    await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonOptions, cancellationToken)
                        .ConfigureAwait(false);
                }

                foreach (var item in manifest.Entries)
                {
                    if (!sourceFiles.TryGetValue(item.LogicalId, out var sourcePath)
                        || !File.Exists(sourcePath))
                    {
                        throw new InvalidDataException("TPL_PACKAGE_ENTRY_MISSING");
                    }

                    await using var source = OpenSequentialRead(sourcePath);
                    if (source.Length != item.Length)
                    {
                        throw new InvalidDataException("TPL_PACKAGE_HASH_MISMATCH");
                    }

                    var entry = archive.CreateEntry(item.RelativePath, CompressionLevel.NoCompression);
                    await using var entryStream = entry.Open();
                    await CopyAndVerifyAsync(source, entryStream, item, cancellationToken).ConfigureAwait(false);
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<TemplatePackageImportResult> ValidateAsync(
        string packagePath,
        CancellationToken cancellationToken)
    {
        await using var stream = OpenPackageRead(packagePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var manifest = await ReadAndValidateManifestAsync(archive, cancellationToken).ConfigureAwait(false);
        var entriesByPath = BuildEntryIndex(archive);
        foreach (var item in manifest.Entries)
        {
            var entry = GetValidatedArchiveEntry(entriesByPath, item);
            await using var entryStream = entry.Open();
            await CopyAndVerifyAsync(entryStream, Stream.Null, item, cancellationToken).ConfigureAwait(false);
        }

        return new TemplatePackageImportResult(
            $"group_import_{Guid.NewGuid():N}",
            manifest.RevisionId,
            manifest.RevisionNo,
            manifest.Entries,
            manifest);
    }

    public async Task CopyValidatedEntryAsync(
        string packagePath,
        TemplatePackageEntry entry,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidDataException("TPL_PACKAGE_ENTRY_INVALID");
        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using var packageStream = OpenPackageRead(packagePath);
            using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read);
            var entriesByPath = BuildEntryIndex(archive);
            var archiveEntry = GetValidatedArchiveEntry(entriesByPath, entry);
            await using var input = archiveEntry.Open();
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             BufferSize,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await CopyAndVerifyAsync(input, output, entry, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task<TemplatePackageManifest> ReadAndValidateManifestAsync(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaxEntryCount + 1
            || archive.Entries.Count(entry => string.Equals(
                entry.FullName,
                "manifest.json",
                StringComparison.Ordinal)) != 1)
        {
            throw new InvalidDataException("TPL_PACKAGE_INVALID");
        }

        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("TPL_PACKAGE_INVALID");
        if (manifestEntry.Length <= 0 || manifestEntry.Length > MaxManifestLength || IsSymbolicLink(manifestEntry))
        {
            throw new InvalidDataException("TPL_PACKAGE_INVALID");
        }

        TemplatePackageManifest? manifest;
        try
        {
            await using var manifestStream = manifestEntry.Open();
            await using var bounded = new LengthLimitedReadStream(manifestStream, MaxManifestLength);
            manifest = await JsonSerializer.DeserializeAsync<TemplatePackageManifest>(
                bounded,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("TPL_PACKAGE_INVALID", exception);
        }

        var value = manifest ?? throw new InvalidDataException("TPL_PACKAGE_INVALID");
        ValidateManifest(value);
        ValidateSchema(value);
        var allowedPaths = value.Entries.Select(entry => entry.RelativePath)
            .Append("manifest.json")
            .ToHashSet(StringComparer.Ordinal);
        if (archive.Entries.Any(entry => !allowedPaths.Contains(entry.FullName)))
        {
            throw new InvalidDataException("TPL_PACKAGE_ENTRY_INVALID");
        }
        return value;
    }

    private static Dictionary<string, ZipArchiveEntry> BuildEntryIndex(ZipArchive archive)
    {
        var byPath = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (!byPath.TryAdd(entry.FullName, entry))
            {
                throw new InvalidDataException("TPL_PACKAGE_ENTRY_INVALID");
            }
        }
        return byPath;
    }

    private static ZipArchiveEntry GetValidatedArchiveEntry(
        Dictionary<string, ZipArchiveEntry> entriesByPath,
        TemplatePackageEntry item)
    {
        if (!entriesByPath.TryGetValue(item.RelativePath, out var entry)
            || entry.Length != item.Length
            || !IsSafeRelativePath(entry.FullName)
            || IsSymbolicLink(entry))
        {
            throw new InvalidDataException("TPL_PACKAGE_ENTRY_INVALID");
        }
        return entry;
    }

    private static void ValidateManifest(TemplatePackageManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Protocol))
        {
            throw new InvalidDataException("TPL_PACKAGE_INVALID");
        }
        if (!string.Equals(manifest.Protocol, TemplatePackageProtocol.Version, StringComparison.Ordinal))
        {
            throw new InvalidDataException("TPL_PACKAGE_VERSION_UNSUPPORTED");
        }

        if (string.IsNullOrWhiteSpace(manifest.GroupName)
            || manifest.GroupName.Length > 256
            || manifest.RevisionNo <= 0
            || string.IsNullOrWhiteSpace(manifest.RevisionId)
            || string.IsNullOrWhiteSpace(manifest.RuleDigest)
            || manifest.Entries is null
            || manifest.Entries.Count == 0
            || manifest.Entries.Count > MaxEntryCount
            || manifest.Entries.Select(item => item.LogicalId).Distinct(StringComparer.Ordinal).Count()
                != manifest.Entries.Count
            || manifest.Entries.Select(item => item.RelativePath).Distinct(StringComparer.Ordinal).Count()
                != manifest.Entries.Count
            || manifest.Entries.Sum(item => item.Length) > MaxPackageContentLength
            || manifest.Entries.Any(item => string.IsNullOrWhiteSpace(item.LogicalId)
                || !IsSafeRelativePath(item.RelativePath)
                || item.RelativePath.Equals("manifest.json", StringComparison.Ordinal)
                || item.Length <= 0
                || item.Length > MaxEntryLength
                || item.Sha256.Length != 64
                || !item.Sha256.All(Uri.IsHexDigit)
                || item.LogicalId.Contains("key", StringComparison.OrdinalIgnoreCase)
                || item.LogicalId.Contains("credential", StringComparison.OrdinalIgnoreCase)
                || item.RelativePath.Contains("photo", StringComparison.OrdinalIgnoreCase)
                || item.RelativePath.Contains("history", StringComparison.OrdinalIgnoreCase))
            || manifest.Pages is null
            || manifest.Pages.Count == 0
            || manifest.Pages.Select(page => page.PageId).Distinct(StringComparer.Ordinal).Count()
                != manifest.Pages.Count
            || manifest.Pages.Select(page => page.Order).Distinct().Count() != manifest.Pages.Count
            || manifest.Pages.Any(page => string.IsNullOrWhiteSpace(page.PageId)
                || string.IsNullOrWhiteSpace(page.Name)
                || !manifest.Entries.Any(entry => string.Equals(
                    entry.LogicalId,
                    page.ResourceLogicalId,
                    StringComparison.Ordinal))
                || (page.GenerationMode == TemplateGenerationModeOption.VariantCapacityCombination
                    ? page.VariantCapacity is null or <= 0
                    : page.VariantCapacity is not null)))
        {
            throw new InvalidDataException("TPL_PACKAGE_INVALID");
        }

        var entriesById = manifest.Entries.ToDictionary(entry => entry.LogicalId, StringComparer.Ordinal);
        var frameIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in manifest.Pages)
        {
            var entry = entriesById[page.ResourceLogicalId];
            var inspection = page.Inspection;
            if (!IsValidInspection(inspection, entry)
                || (page.Enabled
                    && page.ContentType == TemplatePageContentTypeOption.NormalComposition
                    && (page.Bindings is null || page.Bindings.Count == 0)))
            {
                throw new InvalidDataException("TPL_PACKAGE_INVALID");
            }

            foreach (var binding in page.Bindings ?? [])
            {
                var layer = inspection!.Layers.SingleOrDefault(candidate =>
                    candidate.LayerId == binding.LayerId
                    && candidate.Path.SequenceEqual(binding.LayerPath));
                if (!frameIds.Add(binding.FrameId)
                    || !string.Equals(binding.PageId, page.PageId, StringComparison.Ordinal)
                    || !string.Equals(binding.StructureSignature, inspection.StructureSignature, StringComparison.OrdinalIgnoreCase)
                    || layer?.SmartObject is null
                    || !string.Equals(binding.LayerName, layer.Name, StringComparison.Ordinal)
                    || binding.Visible != layer.Visible
                    || binding.Clipping != layer.Clipping
                    || binding.UserMask != layer.HasMask
                    || binding.VectorMask != layer.HasVectorMask
                    || binding.Opacity != layer.Opacity
                    || !string.Equals(binding.BlendMode, layer.BlendMode, StringComparison.Ordinal)
                    || !binding.SelectionCriteria.Any(criterion => string.Equals(
                        criterion.Key,
                        "asset_id",
                        StringComparison.Ordinal)))
                {
                    throw new InvalidDataException("TPL_PACKAGE_INVALID");
                }
            }
        }
    }

    private static bool IsValidInspection(
        Desktop.Application.Psd.PsdInspectionDocument? inspection,
        TemplatePackageEntry entry)
    {
        if (inspection?.Canvas is null
            || inspection.Layers is null
            || inspection.Timing is null
            || inspection.SizeBytes != entry.Length
            || !string.Equals(inspection.FileSha256, entry.Sha256, StringComparison.OrdinalIgnoreCase)
            || inspection.Canvas.Width <= 0
            || inspection.Canvas.Height <= 0
            || inspection.Canvas.Depth <= 0
            || string.IsNullOrWhiteSpace(inspection.Canvas.ColorMode)
            || inspection.LayerCount != inspection.Layers.Count
            || inspection.SmartObjectCount != inspection.Layers.Count(layer => layer.SmartObject is not null)
            || inspection.MaskCount != inspection.Layers.Count(layer => layer.HasMask)
            || inspection.VectorMaskCount != inspection.Layers.Count(layer => layer.HasVectorMask)
            || inspection.ClippingCount != inspection.Layers.Count(layer => layer.Clipping)
            || inspection.ProcessPeakWorkingSetBytes < 0
            || inspection.Timing.OpenMilliseconds < 0
            || inspection.Timing.InspectMilliseconds < 0
            || inspection.Timing.TotalMilliseconds < 0
            || inspection.StructureSignature.Length != 71
            || !inspection.StructureSignature.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            || !inspection.StructureSignature["sha256:".Length..].All(Uri.IsHexDigit))
        {
            return false;
        }

        return inspection.Layers.All(layer =>
            layer.Path is { Count: > 0 }
            && layer.LayerId > 0
            && !string.IsNullOrWhiteSpace(layer.Name)
            && !string.IsNullOrWhiteSpace(layer.Kind)
            && layer.Opacity is >= 0 and <= 255
            && layer.FillOpacity is >= 0 and <= 255
            && !string.IsNullOrWhiteSpace(layer.BlendMode)
            && layer.Bounds is not null);
    }

    private static bool IsSafeRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && !path.StartsWith('/')
        && !path.Contains("..", StringComparison.Ordinal)
        && !path.Contains('\\', StringComparison.Ordinal)
        && !Path.IsPathRooted(path)
        && !path.Contains(':', StringComparison.Ordinal);

    private static bool IsSymbolicLink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    private static FileStream OpenPackageRead(string packagePath)
    {
        if (!File.Exists(packagePath))
        {
            throw new InvalidDataException("TPL_PACKAGE_INVALID");
        }
        return OpenSequentialRead(packagePath);
    }

    private static FileStream OpenSequentialRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        BufferSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task CopyAndVerifyAsync(
        Stream input,
        Stream output,
        TemplatePackageEntry item,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                total = checked(total + read);
                if (total > item.Length)
                {
                    throw new InvalidDataException("TPL_PACKAGE_HASH_MISMATCH");
                }
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            var actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (total != item.Length
                || !string.Equals(item.Sha256, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("TPL_PACKAGE_HASH_MISMATCH");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ValidateSchema(TemplatePackageManifest manifest)
    {
        if (contracts is null)
        {
            return;
        }

        var result = contracts.Validate(
            ContractDocumentKind.TemplatePackageManifest,
            JsonSerializer.Serialize(manifest, JsonOptions));
        if (!result.IsValid)
        {
            throw new InvalidDataException("TPL_PACKAGE_INVALID");
        }
    }

    private sealed class LengthLimitedReadStream(Stream inner, long maximumLength) : Stream
    {
        private long _read;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Account(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Account(read);
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        private void Account(int read)
        {
            _read = checked(_read + read);
            if (_read > maximumLength)
            {
                throw new InvalidDataException("TPL_PACKAGE_INVALID");
            }
        }
    }
}
