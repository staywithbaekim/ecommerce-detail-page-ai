using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Desktop.Domain.Inputs;

namespace Desktop.Infrastructure.Bridge;

internal sealed record RenderStyleOutputTarget(string BatchId, string StyleId, string SourceDirectory,
    string ParentDirectory, string DirectoryName, string FinalDirectory, string StagingDirectory);

internal sealed class RenderOutputTargetException(string code, string? styleId = null, Exception? innerException = null) : IOException(code, innerException)
{
    internal string Code { get; } = code;
    internal string? StyleId { get; } = styleId;
}

/// <summary>Initial public destinations only. No legacy output rewrite, renderer work or filesystem writes.</summary>
internal static class RenderStyleOutputPlanner
{
    internal const string SourceOutputDirectoryName = "生成详情页";

    internal static ImmutableArray<RenderStyleOutputTarget> Plan(BatchDraft batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.ScanStatus != BatchScanStatus.ScanReady || batch.OutputSelection is null)
            throw new RenderOutputTargetException("RENDER_OUTPUT_SELECTION_NOT_CONFIRMED");
        var sourceRoot = LocalDirectoryPath(batch.InputRoot);
        var evidenceRoot = LocalDirectoryPath(batch.OutputRoot);
        var unifiedRoot = batch.OutputSelection.Mode == BatchOutputMode.UnifiedDirectory
            ? LocalDirectoryPath(batch.OutputSelection.UnifiedRoot!) : null;
        var names = DirectoryNames(batch.Styles);
        var result = ImmutableArray.CreateBuilder<RenderStyleOutputTarget>(batch.Styles.Length);
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var style in batch.Styles.OrderBy(item => item.Order).ThenBy(item => item.StyleId, StringComparer.Ordinal))
        {
            // The scanner owns only the selected root or one direct style child, never guessed ancestors.
            var relative = style.SourceDirectoryRelativePath;
            if (batch.InputType == BatchInputType.SingleStyle ? relative.Length != 0
                : relative.Length == 0 || relative.Contains('/') || relative.Contains('\\'))
                throw new RenderOutputTargetException("RENDER_OUTPUT_SOURCE_MAPPING_INVALID", style.StyleId);
            if (relative.Length != 0) ValidateLeaf(relative);
            var source = relative.Length == 0 ? sourceRoot : LocalDirectoryPath(Path.Combine(sourceRoot, relative));
            if (!sources.Add(source)) throw new RenderOutputTargetException("RENDER_OUTPUT_SOURCE_MAPPING_INVALID", style.StyleId);
            var parent = unifiedRoot ?? source;
            var name = unifiedRoot is null ? SourceOutputDirectoryName : names[style.StyleId];
            var final = LocalDirectoryPath(Path.Combine(parent, name));
            var stageId = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                new { batch.BatchId, style.StyleId })));
            var staging = LocalDirectoryPath(Path.Combine(parent, ".edp-stage-" + stageId));
            if (Overlaps(final, evidenceRoot) || Overlaps(staging, evidenceRoot))
                throw new RenderOutputTargetException("RENDER_OUTPUT_EVIDENCE_DIRECTORY_OVERLAP", style.StyleId);
            result.Add(new(batch.BatchId, style.StyleId, source, parent, name, final, staging));
        }
        foreach (var target in result)
        {
            // Descendants of a source are intentional in source mode. An output must never contain a source.
            if (sources.Any(source => Within(source, target.FinalDirectory) || Within(source, target.StagingDirectory)))
                throw new RenderOutputTargetException("RENDER_OUTPUT_SOURCE_DIRECTORY_OVERLAP", target.StyleId);
            if (result.Any(other => other.StyleId != target.StyleId
                && (Overlaps(other.FinalDirectory, target.FinalDirectory)
                    || Overlaps(other.StagingDirectory, target.StagingDirectory)
                    || Overlaps(other.FinalDirectory, target.StagingDirectory))))
                throw new RenderOutputTargetException("RENDER_OUTPUT_TARGET_COLLISION", target.StyleId);
        }
        return result.MoveToImmutable();
    }

    internal static RenderStyleOutputPreflight Preflight(BatchDraft batch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var targets = Plan(batch);
        var leases = new Dictionary<string, RenderOutputDirectoryLease>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // A source of an earlier style may also be a later style's public parent.
            // Acquire all writable parents before adding read-only source leases.
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!leases.ContainsKey(target.ParentDirectory))
                        leases.Add(target.ParentDirectory, RenderOutputDirectoryLease.Open(target.ParentDirectory, true));
                }
                catch (RenderOutputTargetException error) when (error.StyleId is null)
                { throw new RenderOutputTargetException(error.Code, target.StyleId); }
            }
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!leases.ContainsKey(target.SourceDirectory))
                        leases.Add(target.SourceDirectory, RenderOutputDirectoryLease.Open(target.SourceDirectory));
                    RequireAbsent(target.FinalDirectory, target.StyleId);
                    RequireAbsent(target.StagingDirectory, target.StyleId);
                }
                catch (RenderOutputTargetException error) when (error.StyleId is null)
                { throw new RenderOutputTargetException(error.Code, target.StyleId); }
            }
            var evidenceDirectory = LocalDirectoryPath(batch.OutputRoot);
            ValidatePhysicalBoundaries(targets, evidenceDirectory, leases);
            cancellationToken.ThrowIfCancellationRequested();
            return new(targets, evidenceDirectory, leases);
        }
        catch
        {
            foreach (var lease in leases.Values) lease.Dispose();
            throw;
        }
    }

    internal static string PinExistingEvidenceAncestor(string evidenceDirectory,
        Dictionary<string, RenderOutputDirectoryLease> leases)
    {
        // The private root may not exist yet. Never create it, follow an alias/link,
        // or leave its existing ancestry unpinned merely because the leaf is absent.
        for (var path = evidenceDirectory; path is not null; path = Path.GetDirectoryName(path))
        {
            if (leases.TryGetValue(path, out var existing)) { existing.Verify(); return Resolve(existing, path); }
            try
            {
                var lease = RenderOutputDirectoryLease.Open(path, allowVirtualizedLocalAppData: true);
                leases.Add(path, lease);
                return Resolve(lease, path);
            }
            catch (RenderOutputTargetException error) when (error.Code == "RENDER_OUTPUT_DIRECTORY_UNAVAILABLE"
                && Path.GetDirectoryName(path) is not null) { }
        }
        throw new RenderOutputTargetException("RENDER_OUTPUT_DIRECTORY_UNAVAILABLE");

        string Resolve(RenderOutputDirectoryLease lease, string logicalAncestor) => LocalDirectoryPath(
            logicalAncestor.Equals(evidenceDirectory, StringComparison.OrdinalIgnoreCase) ? lease.PhysicalPath
                : Path.Combine(lease.PhysicalPath, Path.GetRelativePath(logicalAncestor, evidenceDirectory)));
    }

    internal static void ValidatePhysicalBoundaries(IEnumerable<RenderStyleOutputTarget> targets, string evidenceDirectory,
        Dictionary<string, RenderOutputDirectoryLease> leases)
    {
        var evidence = PinExistingEvidenceAncestor(evidenceDirectory, leases);
        var resolved = targets.Select(target => new
        {
            target.StyleId, Source = leases[target.SourceDirectory].PhysicalPath,
            Final = Path.Combine(leases[target.ParentDirectory].PhysicalPath, target.DirectoryName),
            Stage = Path.Combine(leases[target.ParentDirectory].PhysicalPath, Path.GetFileName(target.StagingDirectory))
        }).ToArray();
        foreach (var target in resolved)
        {
            if (Overlaps(target.Final, evidence) || Overlaps(target.Stage, evidence))
                throw new RenderOutputTargetException("RENDER_OUTPUT_EVIDENCE_DIRECTORY_OVERLAP", target.StyleId);
            if (resolved.Any(source => Within(source.Source, target.Final) || Within(source.Source, target.Stage)))
                throw new RenderOutputTargetException("RENDER_OUTPUT_SOURCE_DIRECTORY_OVERLAP", target.StyleId);
            if (resolved.Any(other => other.StyleId != target.StyleId && (other.Source.Equals(target.Source, StringComparison.OrdinalIgnoreCase)
                || Overlaps(other.Final, target.Final) || Overlaps(other.Stage, target.Stage) || Overlaps(other.Final, target.Stage))))
                throw new RenderOutputTargetException("RENDER_OUTPUT_TARGET_COLLISION", target.StyleId);
        }
    }

    internal static string LocalDirectoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32_000 || path.Length < 3
            || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/')
            || path[2..].Contains(':')) throw new RenderOutputTargetException("RENDER_OUTPUT_DIRECTORY_INVALID");
        path = path.Replace('/', '\\');
        path = Path.TrimEndingDirectorySeparator(path);
        if (path.Length > 3)
            foreach (var part in path[3..].Split('\\')) ValidateLeaf(part);
        // It is already an absolute, component-validated DOS path. GetFullPath on Windows
        // may expand existing 8.3 names, hiding an alias before the handle checks can reject it.
        return path;
    }

    private static void ValidateLeaf(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name is "." or ".."
            || name.EndsWith(' ') || name.EndsWith('.') || IsDeviceName(name)
            || name.Any(character => char.IsControl(character) || "\\/:*?\"<>|".Contains(character)))
            throw new RenderOutputTargetException("RENDER_OUTPUT_DIRECTORY_INVALID");
    }

    private static bool IsDeviceName(string value)
    {
        var stem = value.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" or "CLOCK$"
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && "123456789¹²³".Contains(stem[3]);
    }

    private static Dictionary<string, string> DirectoryNames(IEnumerable<StyleDraft> styles)
    {
        var proposed = styles.ToDictionary(style => style.StyleId, style => DirectoryName(style.DefaultStyleCode), StringComparer.Ordinal);
        var reserved = new HashSet<string>(proposed.Values, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in proposed.GroupBy(item => item.Value, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() == 1)
            {
                var item = group.Single();
                result.Add(item.Key, item.Value);
                continue;
            }
            var suffix = 1;
            foreach (var item in group.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                string name;
                do { name = item.Value + "-" + (suffix++).ToString("D2", CultureInfo.InvariantCulture); }
                while (!reserved.Add(name));
                result.Add(item.Key, name);
            }
        }
        return result;
    }

    private static string DirectoryName(string value)
    {
        var cleaned = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                || rune.Value == Rune.ReplacementChar.Value || rune.IsAscii && "\\/:*?\"<>|".Contains((char)rune.Value)) cleaned.Append('_');
            else cleaned.Append(rune.ToString());
        }
        var elements = StringInfo.GetTextElementEnumerator(cleaned.ToString().Normalize(NormalizationForm.FormC).Trim(' ', '.', '_'));
        var bounded = new StringBuilder();
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (bounded.Length + element.Length > 96) break;
            bounded.Append(element);
        }
        var name = bounded.ToString().TrimEnd(' ', '.', '_');
        if (name.Length == 0) name = "款式";
        if (IsDeviceName(name)) name = "款式-" + name;
        return name;
    }

    private static bool Overlaps(string left, string right) => Within(left, right) || Within(right, left);
    private static bool Within(string candidate, string root) => candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    internal static void RequireAbsent(string path, string styleId)
    {
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        // Includes empty directories, files, junctions and unregistered leftovers. Never invent a suffix or adopt them.
        throw new RenderOutputTargetException("RENDER_OUTPUT_DIRECTORY_CONFLICT", styleId);
    }
}

internal sealed class RenderStyleOutputPreflight : IDisposable
{
    private readonly Dictionary<string, RenderOutputDirectoryLease> _leases;
    private readonly string _evidenceDirectory;
    private bool _disposed;

    internal RenderStyleOutputPreflight(ImmutableArray<RenderStyleOutputTarget> targets,
        string evidenceDirectory,
        Dictionary<string, RenderOutputDirectoryLease> leases)
    { Targets = targets; _evidenceDirectory = evidenceDirectory; _leases = leases; }

    internal ImmutableArray<RenderStyleOutputTarget> Targets { get; }

    internal RenderOutputDirectoryIdentity GetIdentity(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var lease = _leases[path];
        lease.Verify();
        return lease.Identity;
    }

    internal void RevalidateAvailable(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        RenderStyleOutputPlanner.ValidatePhysicalBoundaries(Targets, _evidenceDirectory, _leases);
        foreach (var target in Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _leases[target.SourceDirectory].Verify();
            _leases[target.ParentDirectory].Verify();
            RenderStyleOutputPlanner.RequireAbsent(target.FinalDirectory, target.StyleId);
            RenderStyleOutputPlanner.RequireAbsent(target.StagingDirectory, target.StyleId);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var lease in _leases.Values) lease.Dispose();
    }
}
