using System.Text.Json;
using Desktop.Application.Files;
using Desktop.Application.Rendering;
using Desktop.Contracts.Bridge;

namespace Desktop.Infrastructure.Bridge;

/// <summary>A checked application-owned output destination. It grants no authority over existing output.</summary>
public sealed partial class BridgeOutputReservation : IDisposable
{
    private readonly FileStream _guard;
    private readonly string _planJson;
    private readonly IPathPolicy _paths;
    private readonly string _stagingRelativePath;
    private readonly string _outputRelativePath;
    private bool _disposed;
    internal RenderTemporaryFileLedger? TemporaryFiles { get; set; }
    private BridgeOutputReservation(string digest, string finalPath, string stagingPath, FileStream guard,
        BridgeOutputBindingV1 binding, bool includePsd, string planJson, IPathPolicy paths, string stagingRelativePath,
        string outputRelativePath)
    {
        PlanDigest = digest; FinalPath = finalPath; StagingPath = stagingPath;
        _guard = guard; Binding = binding;
        IncludePsd = includePsd;
        _planJson = planJson; _paths = paths; _stagingRelativePath = stagingRelativePath;
        _outputRelativePath = outputRelativePath;
    }

    internal string PlanDigest { get; }
    internal string FinalPath { get; }
    internal string StagingPath { get; }
    public BridgeOutputBindingV1 Binding { get; }
    internal bool IncludePsd { get; }
    internal static string InspectionPsdKind => "editable_psd";

    public static BridgeOutputReservation Create(string planJson, string ownedOutputDirectory,
        IPathPolicy paths, IRenderPlanValidator validator)
    {
        if (!validator.Validate(planJson).IsValid) throw Invalid();
        using var plan = JsonDocument.Parse(planJson);
        var output = plan.RootElement.GetProperty("output");
        // Existing formal outputs are always protected in the current task. Confirmed overwrite
        // is not implemented by interpreting a logical target ID as an operating-system path.
        if (output.GetProperty("overwrite_policy").GetString() != "never") throw Invalid();
        var root = paths.ResolveOwnedPath(ownedOutputDirectory);
        ValidatePath(root);
        Directory.CreateDirectory(root);
        ValidatePath(root);
        var artifact = output.GetProperty("artifact_set_id").GetString()!;
        if (artifact.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))
            || artifact.EndsWith('.') || artifact.Split('.')[0].ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL"
            || System.Text.RegularExpressions.Regex.IsMatch(artifact, @"^(COM|LPT)[1-9](\.|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)) throw Invalid();
        var finalPath = Path.Combine(root, artifact);
        if (Directory.Exists(finalPath) || File.Exists(finalPath)) throw Invalid();
        var stagingRelative = RenderPageCommitter.StagingDirectory(ownedOutputDirectory,
            plan.RootElement.GetProperty("batch_id").GetString()!, plan.RootElement.GetProperty("product_id").GetString()!,
            plan.RootElement.GetProperty("document").GetProperty("page_id").GetString()!,
            plan.RootElement.GetProperty("plan_digest").GetString()!);
        var staging = paths.ResolveOwnedPath(stagingRelative);
        // A sibling target lock stays held while the page directory is moved.
        var guard = RenderPageCommitter.AcquireGuard(paths, ownedOutputDirectory, artifact);
        try
        {
            if (Directory.Exists(staging) || File.Exists(staging)
                || Directory.Exists(finalPath) || File.Exists(finalPath)) throw Invalid();
            ValidatePath(staging);
            Directory.CreateDirectory(staging);
            ValidatePath(staging);
            guard.WriteByte(1); guard.Flush(true);
            return new(plan.RootElement.GetProperty("plan_digest").GetString()!, finalPath, staging, guard,
                new(artifact, BridgeAdapterCryptography.RandomToken(), "never", []),
                output.GetProperty("editable_psd").GetProperty("enabled").GetBoolean(), planJson, paths,
                stagingRelative, ownedOutputDirectory);
        }
        catch { guard.Dispose(); throw; }
    }

    internal void EnsureMatches(string digest, BridgeOutputBindingV1 binding)
    {
        if (_disposed || !string.Equals(PlanDigest, digest, StringComparison.OrdinalIgnoreCase)
            || binding != Binding || Directory.Exists(FinalPath) || File.Exists(FinalPath)) throw Invalid();
        ValidatePath(StagingPath);
    }

    internal static void ValidatePath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.Length < 3 || path[1] != ':' || path[2..].Contains(':')) throw Invalid();
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Invalid();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var upload in _uploads.Values) upload.Stream.Dispose();
        _guard.Dispose();
        // DeleteOnClose releases only our marker handle, without a path-based delete race.
        // Preserve the task directory and any outputs for recovery.
    }

    // Only the broker may call this after durable explicit cancellation/failure or verified commit.
    // Dispose alone (disconnect, uncertain save or process shutdown) retains recovery evidence.
    internal void ReleaseAfterTerminal()
    {
        Dispose();
        PreparedRenderResources.Release(_uploads.Values.Select(upload => upload.OwnedFile));
    }
    public override string ToString() => nameof(BridgeOutputReservation);
    private static InvalidDataException Invalid() => new("BRIDGE_RESOURCE_BINDING_INVALID");
}
