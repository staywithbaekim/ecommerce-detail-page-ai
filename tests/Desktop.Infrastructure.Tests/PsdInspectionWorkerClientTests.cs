using Desktop.Application.Psd;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Psd;
using System.Text;
using System.Text.Json;

namespace Desktop.Infrastructure.Tests;

public sealed class PsdInspectionWorkerClientTests
{
    private static readonly int[] RootLayerPath = [0];
    private static readonly int[] TenByTenBounds = [0, 0, 10, 10];
    private static readonly double[] TenByTenTransform = [0, 0, 10, 0, 10, 10, 0, 10];

    [Fact]
    public async Task RejectsPathOutsideApplicationRootBeforeWorkerLaunch()
    {
        using var directory = new TestDirectory();
        var client = CreateClient(directory.Path);

        var result = await client.InspectAsync(
            new PsdInspectionRequest("request-01", "..\\outside.psd"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("TPL_PSD_PATH_NOT_OWNED", result.Error!.Code);
    }

    [Fact]
    public async Task ReportsMissingInternalCopyBeforeWorkerLaunch()
    {
        using var directory = new TestDirectory();
        var client = CreateClient(directory.Path);

        var result = await client.InspectAsync(
            new PsdInspectionRequest("request-02", "templates\\missing.psd"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("TPL_SOURCE_UNREADABLE", result.Error!.Code);
        Assert.True(result.Error.Retryable);
    }

    [Fact]
    public async Task ReportsMissingWorkerWithoutReadingSourceContents()
    {
        using var directory = new TestDirectory();
        var inputPath = Path.Combine(directory.Path, "templates", "internal.psd");
        Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
        await File.WriteAllBytesAsync(inputPath, [1, 2, 3]);
        var client = CreateClient(directory.Path);

        var result = await client.InspectAsync(
            new PsdInspectionRequest("request-03", "templates\\internal.psd"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("TPL_PSD_WORKER_UNAVAILABLE", result.Error!.Code);
    }

    [Fact]
    public async Task RejectsMalformedLayerBoundsAsInvalidResponse()
    {
        using var directory = new TestDirectory();
        await CreateInternalCopy(directory.Path);
        var response = ValidResponse("request-04", [0, 0, 10]);
        var client = CreateScriptClient(directory.Path, WriteResponseScript(response));

        var result = await client.InspectAsync(
            new PsdInspectionRequest("request-04", "templates\\internal.psd"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("TPL_PSD_WORKER_INVALID_RESPONSE", result.Error!.Code);
    }

    [Fact]
    public async Task StopsWorkerAfterTimeout()
    {
        using var directory = new TestDirectory();
        await CreateInternalCopy(directory.Path);
        var client = CreateScriptClient(
            directory.Path,
            "[Console]::In.ReadToEnd() | Out-Null; Start-Sleep -Seconds 5",
            timeout: TimeSpan.FromMilliseconds(500));

        var result = await client.InspectAsync(
            new PsdInspectionRequest("request-05", "templates\\internal.psd"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("TPL_PSD_WORKER_TIMEOUT", result.Error!.Code);
        Assert.True(result.Error.Retryable);
    }

    [Fact]
    public async Task RejectsResponseOverConfiguredLimit()
    {
        using var directory = new TestDirectory();
        await CreateInternalCopy(directory.Path);
        var client = CreateScriptClient(
            directory.Path,
            "[Console]::In.ReadToEnd() | Out-Null; [Console]::Out.Write('x' * 8192)",
            maxResponseBytes: 128);

        var result = await client.InspectAsync(
            new PsdInspectionRequest("request-06", "templates\\internal.psd"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("TPL_PSD_WORKER_RESPONSE_TOO_LARGE", result.Error!.Code);
    }

    [Fact]
    public async Task MapsEmbeddedPayloadDigestFromWorkerResponse()
    {
        using var directory = new TestDirectory();
        await CreateInternalCopy(directory.Path);
        var response = ValidResponseWithEmbeddedPayload("request-07", new string('C', 64));
        var client = CreateScriptClient(directory.Path, WriteResponseScript(response));

        var result = await client.InspectAsync(
            new PsdInspectionRequest("request-07", "templates\\internal.psd"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var smartObject = Assert.Single(result.Inspection!.Layers).SmartObject!;
        Assert.True(smartObject.EmbeddedContentAvailable);
        Assert.Equal(new string('C', 64), smartObject.EmbeddedContentSha256);
    }

    [Fact]
    public async Task RejectsDigestWhenEmbeddedPayloadIsUnavailable()
    {
        using var directory = new TestDirectory();
        await CreateInternalCopy(directory.Path);
        var response = ValidResponseWithEmbeddedPayload(
            "request-08",
            new string('C', 64),
            available: false);
        var client = CreateScriptClient(directory.Path, WriteResponseScript(response));

        var result = await client.InspectAsync(
            new PsdInspectionRequest("request-08", "templates\\internal.psd"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("TPL_PSD_WORKER_INVALID_RESPONSE", result.Error!.Code);
    }

    private static PsdInspectionWorkerClient CreateClient(string root) => new(
        new LocalAppPathPolicy(root),
        new PsdInspectionWorkerOptions(
            Path.Combine(root, "missing-worker.exe"),
            TimeSpan.FromSeconds(1),
            4096));

    private static PsdInspectionWorkerClient CreateScriptClient(
        string root,
        string script,
        TimeSpan? timeout = null,
        int maxResponseBytes = 4096)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var executable = Path.Combine(
            windows,
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        return new PsdInspectionWorkerClient(
            new LocalAppPathPolicy(root),
            new PsdInspectionWorkerOptions(
                executable,
                timeout ?? TimeSpan.FromSeconds(10),
                maxResponseBytes,
                ["-NoProfile", "-NonInteractive", "-Command", script]));
    }

    private static async Task CreateInternalCopy(string root)
    {
        var inputPath = Path.Combine(root, "templates", "internal.psd");
        Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
        await File.WriteAllBytesAsync(inputPath, [1, 2, 3]);
    }

    private static string WriteResponseScript(string response)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(response));
        return $"[Console]::In.ReadToEnd() | Out-Null; [Console]::Out.Write([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encoded}')))";
    }

    private static string ValidResponse(string requestId, int[] bbox) =>
        JsonSerializer.Serialize(
            new
            {
                schema_version = PsdInspectionProtocol.Version,
                request_id = requestId,
                ok = true,
                error = (object?)null,
                inspection = new
                {
                    file_sha256 = new string('A', 64),
                    size_bytes = 3,
                    canvas = new { width = 10, height = 10, depth = 8, color_mode = "RGB" },
                    layer_count = 1,
                    smart_object_count = 0,
                    mask_count = 0,
                    vector_mask_count = 0,
                    clipping_count = 0,
                    structure_signature = new string('B', 64),
                    timing_ms = new { open = 1.0, inspect = 2.0, total = 3.0 },
                    process_peak_working_set_bytes = 1,
                    preview = (object?)null,
                    layers = new[]
                    {
                        new
                        {
                            path = RootLayerPath,
                            layer_id = 1,
                            name = "layer",
                            kind = "pixel",
                            is_group = false,
                            visible = true,
                            effective_visible = true,
                            clipping = false,
                            opacity = 255,
                            fill_opacity = 255,
                            blend_mode = "normal",
                            bbox,
                            has_mask = false,
                            has_vector_mask = false,
                            clipping_base = (object?)null,
                            smart_object = (object?)null,
                        },
                    },
                },
            });

    private static string ValidResponseWithEmbeddedPayload(
        string requestId,
        string digest,
        bool available = true) => JsonSerializer.Serialize(new
        {
            schema_version = PsdInspectionProtocol.Version,
            request_id = requestId,
            ok = true,
            error = (object?)null,
            inspection = new
            {
                file_sha256 = new string('A', 64),
                size_bytes = 3,
                canvas = new { width = 10, height = 10, depth = 8, color_mode = "RGB" },
                layer_count = 1,
                smart_object_count = 1,
                mask_count = 0,
                vector_mask_count = 0,
                clipping_count = 0,
                structure_signature = new string('B', 64),
                timing_ms = new { open = 1.0, inspect = 2.0, total = 3.0 },
                process_peak_working_set_bytes = 1,
                preview = (object?)null,
                layers = new[]
                {
                    new
                    {
                        path = RootLayerPath,
                        layer_id = 1,
                        name = "front",
                        kind = "smartobject",
                        is_group = false,
                        visible = true,
                        effective_visible = true,
                        clipping = false,
                        opacity = 255,
                        fill_opacity = 255,
                        blend_mode = "normal",
                        bbox = TenByTenBounds,
                        has_mask = false,
                        has_vector_mask = false,
                        clipping_base = (object?)null,
                        smart_object = new
                        {
                            unique_id = "so_01",
                            filename = "front.png",
                            kind = "data",
                            file_type = "png",
                            detected_file_type = "png",
                            file_size = 3,
                            is_psd = false,
                            transform_box = TenByTenTransform,
                            embedded_content_sha256 = digest,
                            embedded_content_available = available
                        }
                    }
                }
            }
        });
}
