using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Desktop.Application.Files;
using Desktop.Application.Psd;

namespace Desktop.Infrastructure.Psd;

public sealed class PsdInspectionWorkerClient : IPsdInspectionPort
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false);

    private readonly IPathPolicy _pathPolicy;
    private readonly PsdInspectionWorkerOptions _options;

    public PsdInspectionWorkerClient(
        IPathPolicy pathPolicy,
        PsdInspectionWorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(pathPolicy);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExecutablePath);
        if (options.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Worker timeout must be positive.");
        }

        if (options.MaxResponseBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Worker response limit must be positive.");
        }

        if (options.Arguments?.Any(string.IsNullOrWhiteSpace) is true)
        {
            throw new ArgumentException("Worker arguments must not contain blank values.", nameof(options));
        }

        _pathPolicy = pathPolicy;
        _options = options;
    }

    public async Task<PsdInspectionResult> InspectAsync(
        PsdInspectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128)
        {
            return Failure(
                "TPL_PSD_REQUEST_INVALID",
                "PSD inspection request id is invalid.",
                "Retry the template import.");
        }

        string inputPath;
        string? previewPath = null;
        try
        {
            inputPath = _pathPolicy.ResolveOwnedPath(request.InternalPsdRelativePath);
            if (!string.IsNullOrWhiteSpace(request.PreviewRelativePath))
            {
                previewPath = _pathPolicy.ResolveOwnedPath(request.PreviewRelativePath);
            }
        }
        catch (InvalidOperationException)
        {
            return Failure(
                "TPL_PSD_PATH_NOT_OWNED",
                "PSD inspection only accepts application-owned paths.",
                "Retry the import so the application can create a verified internal copy.");
        }

        if (!File.Exists(inputPath))
        {
            return Failure(
                "TPL_SOURCE_UNREADABLE",
                "The application-owned PSD copy cannot be read.",
                "Retry the copy or check local storage permissions.",
                retryable: true);
        }

        if (!File.Exists(_options.ExecutablePath))
        {
            return Failure(
                "TPL_PSD_WORKER_UNAVAILABLE",
                "The PSD inspection component is unavailable.",
                "Repair or reinstall the desktop application.");
        }

        var workerRequest = new WorkerRequest(
            PsdInspectionProtocol.Version,
            request.RequestId,
            WorkerFilePath(inputPath),
            previewPath is null ? null : WorkerFilePath(previewPath),
            request.IncludeRenderContentEvidence);

        // Convert executable path to short path format (8.3) to avoid PyInstaller
        // issues with non-ASCII characters in paths (e.g., Chinese folder names).
        string executablePath = _options.ExecutablePath;
        string workingDirectory = Path.GetDirectoryName(_options.ExecutablePath)!;
        if (OperatingSystem.IsWindows())
        {
            executablePath = GetShortPathName(executablePath) ?? executablePath;
            workingDirectory = GetShortPathName(workingDirectory) ?? workingDirectory;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8WithoutBom,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (_options.Arguments is not null)
        {
            foreach (var argument in _options.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return WorkerUnavailable();
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return WorkerUnavailable();
        }

        var responseTask = ReadBoundedAsync(
            process.StandardOutput,
            _options.MaxResponseBytes,
            CancellationToken.None);
        var errorTask = ReadBoundedAsync(
            process.StandardError,
            64 * 1024,
            CancellationToken.None);
        var requestJson = JsonSerializer.Serialize(workerRequest, SerializerOptions);
        await process.StandardInput.WriteAsync(requestJson.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.WriteLineAsync().ConfigureAwait(false);
        process.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        string responseText;
        try
        {
            var exitTask = process.WaitForExitAsync(timeout.Token);
            var completedTask = await Task.WhenAny(
                exitTask,
                responseTask,
                errorTask).ConfigureAwait(false);
            if (completedTask == responseTask)
            {
                _ = await responseTask.ConfigureAwait(false);
            }
            else if (completedTask == errorTask)
            {
                _ = await errorTask.ConfigureAwait(false);
            }

            await exitTask.ConfigureAwait(false);
            responseText = await responseTask.ConfigureAwait(false);
            _ = await errorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return Failure(
                "TPL_PSD_WORKER_TIMEOUT",
                "PSD inspection took too long and was stopped.",
                "Retry or run Photoshop compatibility inspection.",
                retryable: true);
        }
        catch (WorkerOutputLimitException)
        {
            TryKill(process);
            return Failure(
                "TPL_PSD_WORKER_RESPONSE_TOO_LARGE",
                "PSD inspection returned too much data.",
                "Run Photoshop compatibility inspection for this PSD.");
        }

        if (process.ExitCode != 0)
        {
            return Failure(
                "TPL_PSD_WORKER_FAILED",
                "PSD inspection stopped unexpectedly.",
                "Retry or repair the desktop application.",
                retryable: true);
        }

        WorkerResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<WorkerResponse>(responseText, SerializerOptions);
        }
        catch (JsonException)
        {
            return InvalidResponse();
        }

        if (response is null ||
            !string.Equals(response.SchemaVersion, PsdInspectionProtocol.Version, StringComparison.Ordinal) ||
            !string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
        {
            return InvalidResponse();
        }

        if (!response.Ok)
        {
            return response.Inspection is not null || !IsValid(response.Error)
                ? InvalidResponse()
                : PsdInspectionResult.Failure(new PsdInspectionError(
                    response.Error!.Code!,
                    response.Error.Message!,
                    response.Error.UserAction!,
                    response.Error.Retryable));
        }

        if (response.Error is not null || !IsValid(response.Inspection))
        {
            return InvalidResponse();
        }

        try
        {
            return PsdInspectionResult.Success(Map(response.Inspection!));
        }
        catch (Exception error) when (error is ArgumentException or IndexOutOfRangeException or NullReferenceException)
        {
            return InvalidResponse();
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maxBytes, 64 * 1024));
        var buffer = new char[4096];
        var observedBytes = 0;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return builder.ToString();
            }

            observedBytes += Encoding.UTF8.GetByteCount(buffer.AsSpan(0, count));
            if (observedBytes > maxBytes)
            {
                throw new WorkerOutputLimitException();
            }

            builder.Append(buffer, 0, count);
        }
    }

    private static PsdInspectionDocument Map(WorkerInspection inspection) => new(
        inspection.FileSha256!,
        inspection.SizeBytes,
        new PsdCanvasInspection(
            inspection.Canvas!.Width,
            inspection.Canvas.Height,
            inspection.Canvas.Depth,
            inspection.Canvas.ColorMode!),
        inspection.LayerCount,
        inspection.SmartObjectCount,
        inspection.MaskCount,
        inspection.VectorMaskCount,
        inspection.ClippingCount,
        inspection.StructureSignature!,
        new PsdInspectionTiming(
            inspection.TimingMs!.Open,
            inspection.TimingMs.Inspect,
            inspection.TimingMs.Total),
        inspection.ProcessPeakWorkingSetBytes,
        inspection.Preview is null
            ? null
            : new PsdPreviewInspection(
                inspection.Preview.Width,
                inspection.Preview.Height,
                inspection.Preview.Mode!,
                inspection.Preview.Sha256!,
                inspection.Preview.SizeBytes),
        inspection.Layers!.Select(Map).ToArray());

    private static PsdLayerInspection Map(WorkerLayer layer) => new(
        layer.Path!,
        layer.LayerId,
        layer.Name!,
        layer.Kind!,
        layer.IsGroup,
        layer.Visible,
        layer.EffectiveVisible,
        layer.Clipping,
        layer.Opacity,
        layer.FillOpacity,
        layer.BlendMode!,
        Bounds(layer.Bbox!),
        layer.HasMask,
        layer.HasVectorMask,
        layer.ClippingBase is null
            ? null
            : new PsdClippingBaseInspection(
                layer.ClippingBase.Path!,
                layer.ClippingBase.LayerId,
                layer.ClippingBase.Name!,
                layer.ClippingBase.Kind!,
                layer.ClippingBase.Visible,
                layer.ClippingBase.EffectiveVisible,
                Bounds(layer.ClippingBase.Bbox!),
                layer.ClippingBase.HasMask,
                layer.ClippingBase.HasVectorMask,
                layer.ClippingBase.Opacity,
                layer.ClippingBase.BlendMode!),
        layer.SmartObject is null
            ? null
            : new PsdSmartObjectInspection(
                layer.SmartObject.UniqueId!,
                layer.SmartObject.Filename!,
                layer.SmartObject.Kind!,
                layer.SmartObject.FileType,
                layer.SmartObject.DetectedFileType!,
                layer.SmartObject.FileSize,
                layer.SmartObject.IsPsd,
                layer.SmartObject.TransformBox!)
            {
                EmbeddedContentSha256 = layer.SmartObject.EmbeddedContentSha256,
                EmbeddedContentAvailable = layer.SmartObject.EmbeddedContentAvailable
            })
    {
        ContentEvidence = layer.ContentEvidence,
        PixelEvidence = layer.PixelEvidence is null
            ? new PsdLayerPixelEvidence(false, 0, 0, null)
            : new PsdLayerPixelEvidence(
                layer.PixelEvidence.Available,
                layer.PixelEvidence.RenderedPixelCount,
                layer.PixelEvidence.CompositeContributionPixelCount,
                layer.PixelEvidence.CompositeContributionBbox is null
                    ? null
                    : Bounds(layer.PixelEvidence.CompositeContributionBbox))
            { RasterImage = layer.PixelEvidence.RasterImage }
    };

    private static PsdLayerBounds Bounds(int[] values) => new(
        values[0],
        values[1],
        values[2],
        values[3]);

    private static bool IsValid(WorkerError? error) =>
        error is not null &&
        !string.IsNullOrWhiteSpace(error.Code) &&
        error.Code.StartsWith("TPL_", StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(error.Message) &&
        !string.IsNullOrWhiteSpace(error.UserAction);

    private static bool IsValid(WorkerInspection? inspection)
    {
        if (inspection is null ||
            !IsSha256(inspection.FileSha256) ||
            inspection.SizeBytes <= 0 ||
            inspection.Canvas is null ||
            inspection.Canvas.Width <= 0 ||
            inspection.Canvas.Height <= 0 ||
            inspection.Canvas.Depth <= 0 ||
            string.IsNullOrWhiteSpace(inspection.Canvas.ColorMode) ||
            inspection.LayerCount < 0 ||
            inspection.SmartObjectCount < 0 ||
            inspection.MaskCount < 0 ||
            inspection.VectorMaskCount < 0 ||
            inspection.ClippingCount < 0 ||
            !IsSha256(inspection.StructureSignature) ||
            inspection.TimingMs is null ||
            inspection.TimingMs.Open < 0 ||
            inspection.TimingMs.Inspect < 0 ||
            inspection.TimingMs.Total < 0 ||
            inspection.ProcessPeakWorkingSetBytes < 0 ||
            inspection.Layers is null ||
            inspection.LayerCount != inspection.Layers.Length ||
            inspection.Layers.Any(layer => !IsValid(layer)) ||
            inspection.Layers.Select(layer => string.Join('/', layer.Path!)).Distinct(StringComparer.Ordinal).Count() != inspection.Layers.Length)
        {
            return false;
        }

        if (inspection.SmartObjectCount != inspection.Layers.Count(layer => layer.SmartObject is not null) ||
            inspection.MaskCount != inspection.Layers.Count(layer => layer.HasMask) ||
            inspection.VectorMaskCount != inspection.Layers.Count(layer => layer.HasVectorMask) ||
            inspection.ClippingCount != inspection.Layers.Count(layer => layer.Clipping))
        {
            return false;
        }

        return inspection.Preview is null ||
            (inspection.Preview.Width > 0 &&
             inspection.Preview.Height > 0 &&
             !string.IsNullOrWhiteSpace(inspection.Preview.Mode) &&
             IsSha256(inspection.Preview.Sha256) &&
             inspection.Preview.SizeBytes > 0);
    }

    private static bool IsValid(WorkerLayer? layer)
    {
        if (layer is null ||
            layer.Path is null ||
            layer.Path.Length == 0 ||
            layer.Path.Any(value => value < 0) ||
            layer.LayerId <= 0 ||
            string.IsNullOrWhiteSpace(layer.Name) ||
            string.IsNullOrWhiteSpace(layer.Kind) ||
            layer.Opacity is < 0 or > 255 ||
            layer.FillOpacity is < 0 or > 255 ||
            string.IsNullOrWhiteSpace(layer.BlendMode) ||
            !IsBounds(layer.Bbox) ||
            layer.Clipping != (layer.ClippingBase is not null))
        {
            return false;
        }

        if (layer.ClippingBase is not null && !IsValid(layer.ClippingBase))
        {
            return false;
        }

        if (layer.PixelEvidence is not null && !IsValid(layer.PixelEvidence))
        {
            return false;
        }
        if (layer.ContentEvidence is { } content &&
            new[] { content.PixelSha256, content.TextSha256, content.MaskSha256, content.VectorMaskSha256,
                content.EffectsSha256, content.TextStyleSha256, content.AdjustmentSha256 }
                .Any(hash => hash is not null && !IsSha256(hash))) return false;

        return layer.SmartObject is null ||
            (!string.IsNullOrWhiteSpace(layer.SmartObject.UniqueId) &&
             !string.IsNullOrWhiteSpace(layer.SmartObject.Filename) &&
             !string.IsNullOrWhiteSpace(layer.SmartObject.Kind) &&
             !string.IsNullOrWhiteSpace(layer.SmartObject.DetectedFileType) &&
             layer.SmartObject.FileSize >= 0 &&
             layer.SmartObject.TransformBox is { Length: 8 } &&
             layer.SmartObject.TransformBox.All(double.IsFinite) &&
             (layer.SmartObject.EmbeddedContentAvailable
                 ? IsSha256(layer.SmartObject.EmbeddedContentSha256)
                 : layer.SmartObject.EmbeddedContentSha256 is null));
    }

    private static bool IsValid(WorkerLayerPixelEvidence evidence) =>
        (evidence.RasterImage is null || evidence.RasterImage.ColorCount is >= 0 and <= 9216
            && double.IsFinite(evidence.RasterImage.DetailPixelRatio)
            && evidence.RasterImage.DetailPixelRatio is >= 0 and <= 1) &&
        evidence.RenderedPixelCount >= 0 &&
        evidence.CompositeContributionPixelCount >= 0 &&
        (evidence.Available ||
            (evidence.RenderedPixelCount == 0 &&
             evidence.CompositeContributionPixelCount == 0 &&
             evidence.CompositeContributionBbox is null)) &&
        (evidence.CompositeContributionPixelCount == 0
            ? evidence.CompositeContributionBbox is null
            : IsBounds(evidence.CompositeContributionBbox));

    private static bool IsValid(WorkerClippingBase clippingBase) =>
        clippingBase.Path is { Length: > 0 } &&
        clippingBase.Path.All(value => value >= 0) &&
        clippingBase.LayerId > 0 &&
        !string.IsNullOrWhiteSpace(clippingBase.Name) &&
        !string.IsNullOrWhiteSpace(clippingBase.Kind) &&
        IsBounds(clippingBase.Bbox) &&
        clippingBase.Opacity is >= 0 and <= 255 &&
        !string.IsNullOrWhiteSpace(clippingBase.BlendMode);

    private static bool IsBounds(int[]? values) =>
        values is { Length: 4 } &&
        values[2] >= values[0] &&
        values[3] >= values[1];

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static PsdInspectionResult WorkerUnavailable() => Failure(
        "TPL_PSD_WORKER_UNAVAILABLE",
        "The PSD inspection component could not start.",
        "Repair or reinstall the desktop application.");

    private static PsdInspectionResult InvalidResponse() => Failure(
        "TPL_PSD_WORKER_INVALID_RESPONSE",
        "The PSD inspection component returned an invalid response.",
        "Retry or repair the desktop application.",
        retryable: true);

    private static PsdInspectionResult Failure(
        string code,
        string message,
        string userAction,
        bool retryable = false) =>
        PsdInspectionResult.Failure(new PsdInspectionError(
            code,
            message,
            userAction,
            retryable));

    private sealed record WorkerRequest(
        string SchemaVersion,
        string RequestId,
        string InputPath,
        string? PreviewPath,
        bool IncludeRenderContentEvidence);

    // Python 3.10 on the supported machine cannot open ordinary paths past
    // MAX_PATH. Add the Win32 extended syntax only AFTER ownership resolution;
    // it denotes the same checked file, never a new or less restricted root.
    private static string WorkerFilePath(string path) => OperatingSystem.IsWindows()
        && path.Length >= 248 && Path.IsPathFullyQualified(path) && path[1] == ':'
        ? @"\\?\" + path : path;

    private sealed record WorkerResponse(
        string? SchemaVersion,
        string? RequestId,
        bool Ok,
        WorkerInspection? Inspection,
        WorkerError? Error);

    private sealed record WorkerError(
        string? Code,
        string? Message,
        string? UserAction,
        bool Retryable);

    private sealed record WorkerInspection(
        string? FileSha256,
        long SizeBytes,
        WorkerCanvas? Canvas,
        int LayerCount,
        int SmartObjectCount,
        int MaskCount,
        int VectorMaskCount,
        int ClippingCount,
        string? StructureSignature,
        WorkerTiming? TimingMs,
        long ProcessPeakWorkingSetBytes,
        WorkerPreview? Preview,
        WorkerLayer[]? Layers);

    private sealed record WorkerCanvas(
        int Width,
        int Height,
        int Depth,
        string? ColorMode);

    private sealed record WorkerTiming(
        double Open,
        double Inspect,
        double Total);

    private sealed record WorkerPreview(
        int Width,
        int Height,
        string? Mode,
        string? Sha256,
        long SizeBytes);

    private sealed record WorkerLayer(
        int[]? Path,
        int LayerId,
        string? Name,
        string? Kind,
        bool IsGroup,
        bool Visible,
        bool EffectiveVisible,
        bool Clipping,
        int Opacity,
        int FillOpacity,
        string? BlendMode,
        int[]? Bbox,
        bool HasMask,
        bool HasVectorMask,
        WorkerClippingBase? ClippingBase,
        WorkerSmartObject? SmartObject,
        WorkerLayerPixelEvidence? PixelEvidence,
        PsdLayerContentEvidence? ContentEvidence);

    private sealed record WorkerLayerPixelEvidence(
        bool Available,
        long RenderedPixelCount,
        long CompositeContributionPixelCount,
        int[]? CompositeContributionBbox)
    {
        public PsdRasterImageEvidence? RasterImage { get; init; }
    }

    private sealed record WorkerClippingBase(
        int[]? Path,
        int LayerId,
        string? Name,
        string? Kind,
        bool Visible,
        bool EffectiveVisible,
        int[]? Bbox,
        bool HasMask,
        bool HasVectorMask,
        int Opacity,
        string? BlendMode);

    private sealed record WorkerSmartObject(
        string? UniqueId,
        string? Filename,
        string? Kind,
        string? FileType,
        string? DetectedFileType,
        long FileSize,
        bool IsPsd,
        double[]? TransformBox,
        string? EmbeddedContentSha256,
        bool EmbeddedContentAvailable);

    private sealed class WorkerOutputLimitException : Exception;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetShortPathName(
        string longPath,
        char[] shortPath,
        int bufferSize);

    private static string? GetShortPathName(string longPath)
    {
        if (string.IsNullOrEmpty(longPath))
        {
            return null;
        }

        const int maxPath = 260;
        var shortPath = new char[maxPath];
        var result = GetShortPathName(longPath, shortPath, maxPath);
        if (result == 0 || result >= maxPath)
        {
            return null;
        }

        return new string(shortPath, 0, result);
    }
}
