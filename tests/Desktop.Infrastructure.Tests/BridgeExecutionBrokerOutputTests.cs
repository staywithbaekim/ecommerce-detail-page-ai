using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Desktop.Application.Rendering;
using Desktop.Application.Psd;
using Desktop.Contracts.Bridge;
using Desktop.Contracts.Validation;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Files;

namespace Desktop.Infrastructure.Tests;

public sealed partial class BridgeExecutionBrokerTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("session")]
    [InlineData("claim")]
    [InlineData("digest")]
    [InlineData("handle")]
    [InlineData("artifact")]
    [InlineData("kind")]
    [InlineData("length")]
    [InlineData("offset")]
    [InlineData("hash")]
    [InlineData("count")]
    public async Task CancelledOutputWriteSignalsPendingOnlyForMatchingUnwrittenChunk(string mutation)
    {
        using var broker = Create(new MemoryJournal());
        var (_, output, claim) = await PrepareOutput(broker);
        using var ownedOutput = output;
        await broker.ReportAsync("session_test", Report(claim, 2, "page_started", "running", "page_01"), default);
        byte[] bytes = [255, 216, 255, 0];
        var chunk = Chunk(claim, output, bytes) with { ByteLength = 8 };
        Assert.True((await broker.WriteOutputAsync("session_test", chunk, default)).IsSuccess);
        await broker.CancelAsync(claim.ExecutionId, default);
        chunk = chunk with { Offset = 4 };
        chunk = mutation switch
        {
            "claim" => chunk with { ClaimId = "claim_wrong" },
            "digest" => chunk with { PlanDigest = "sha256:" + new string('0', 64) },
            "handle" => chunk with { OutputRootHandle = new string('A', 43) },
            "artifact" => chunk with { ArtifactSetId = "another_artifact" },
            "kind" => chunk with { OutputKind = "editable_psd" },
            "length" => chunk with { ByteLength = 9 },
            "offset" => chunk with { Offset = 0 },
            "hash" => chunk with { Sha256 = new string('0', 64) },
            "count" => chunk with { Count = 5 },
            _ => chunk
        };
        var result = await broker.WriteOutputAsync(mutation == "session" ? "wrong" : "session_test", chunk, default);
        Assert.Equal(mutation == "valid" ? "BRIDGE_CANCEL_PENDING" : "BRIDGE_RESOURCE_BINDING_INVALID", result.Error!.Code);
        Assert.Null(result.Value);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(output.StagingPath, "*.part"))));
        Assert.Equal("running", (await broker.QueryAsync(claim.ExecutionId, default))!.State);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("session")]
    [InlineData("claim")]
    [InlineData("digest")]
    [InlineData("handle")]
    [InlineData("artifact")]
    [InlineData("kind")]
    [InlineData("length")]
    [InlineData("incomplete")]
    [InlineData("hash_format")]
    [InlineData("faulted")]
    public async Task CancelledOutputSealNeverSealsOrReopensTheFile(string mutation)
    {
        using var broker = Create(new MemoryJournal());
        var (_, output, claim) = await PrepareOutput(broker);
        using var ownedOutput = output;
        await broker.ReportAsync("session_test", Report(claim, 2, "page_started", "running", "page_01"), default);
        var chunk = Chunk(claim, output, [255, 216, 255, 0]);
        if (mutation == "incomplete") chunk = chunk with { ByteLength = 8 };
        await broker.WriteOutputAsync("session_test", chunk, default);
        if (mutation == "faulted") await broker.SealOutputAsync("session_test", Seal(chunk) with { Sha256 = new string('0', 64) }, default);
        await broker.CancelAsync(claim.ExecutionId, default);
        var request = mutation switch
        {
            "claim" => Seal(chunk) with { ClaimId = "claim_wrong" },
            "digest" => Seal(chunk) with { PlanDigest = "sha256:" + new string('0', 64) },
            "handle" => Seal(chunk) with { OutputRootHandle = new string('A', 43) },
            "artifact" => Seal(chunk) with { ArtifactSetId = "another_artifact" },
            "kind" => Seal(chunk) with { OutputKind = "editable_psd" },
            "length" => Seal(chunk) with { ByteLength = 3 },
            "hash_format" => Seal(chunk) with { Sha256 = "invalid" },
            _ => Seal(chunk)
        };
        var result = await broker.SealOutputAsync(mutation == "session" ? "wrong" : "session_test", request, default);
        Assert.Equal(mutation == "valid" ? "BRIDGE_CANCEL_PENDING" : "BRIDGE_RESOURCE_BINDING_INVALID", result.Error!.Code);
        Assert.Null(result.Value);
        Assert.Equal("running", (await broker.QueryAsync(claim.ExecutionId, default))!.State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TemporaryUploadIsReleasedOnlyAfterAcknowledgedCancellation(bool acknowledged)
    {
        using var broker = Create(new MemoryJournal());
        var (fixture, output, claim) = await PrepareOutput(broker);
        using var ownedOutput = output;
        var sources = fixture.Hashes();
        await broker.ReportAsync("session_test", Report(claim, 2, "page_started", "running", "page_01"), default);
        byte[] bytes = [255, 216, 255, 0];
        Assert.True((await broker.WriteOutputAsync("session_test", Chunk(claim, output, bytes), default)).IsSuccess);
        var partial = Assert.Single(Directory.GetFiles(output.StagingPath, "*.part"));
        var unrelated = Path.Combine(output.StagingPath, "unrelated.txt");
        await File.WriteAllTextAsync(unrelated, "keep existing evidence");
        await broker.CancelAsync(claim.ExecutionId, default);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(partial));
        await Assert.ThrowsAsync<InvalidDataException>(() => broker.ReleaseTerminalResourcesAsync(claim.ExecutionId, default));
        if (acknowledged)
        {
            Assert.True((await broker.ReportAsync("session_test", Report(claim, 3, "execution_cancelled", "cancelled"), default)).IsSuccess);
            await broker.ReleaseTerminalResourcesAsync(claim.ExecutionId, default);
            Assert.False(File.Exists(partial));
        }
        else
        {
            await broker.DisconnectAsync(default);
            Assert.Equal("unknown", (await broker.QueryAsync(claim.ExecutionId, default))!.State);
            await Assert.ThrowsAsync<InvalidDataException>(() => broker.ReleaseTerminalResourcesAsync(claim.ExecutionId, default));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(partial));
        }
        Assert.Equal("keep existing evidence", await File.ReadAllTextAsync(unrelated));
        Assert.Equal(sources, fixture.Hashes());
    }

    [Fact]
    public async Task TerminalCleanupRejectsReplacedOutputPathAndKeepsBothFiles()
    {
        using var broker = Create(new MemoryJournal());
        var (_, output, claim) = await PrepareOutput(broker);
        using var ownedOutput = output;
        await broker.ReportAsync("session_test", Report(claim, 2, "page_started", "running", "page_01"), default);
        byte[] bytes = [255, 216, 255, 0];
        await broker.WriteOutputAsync("session_test", Chunk(claim, output, bytes), default);
        var partial = Assert.Single(Directory.GetFiles(output.StagingPath, "*.part"));
        await broker.CancelAsync(claim.ExecutionId, default);
        File.Move(partial, partial + ".retained");
        await File.WriteAllBytesAsync(partial, bytes);
        await broker.ReportAsync("session_test", Report(claim, 3, "execution_cancelled", "cancelled"), default);
        await Assert.ThrowsAsync<IOException>(() => broker.ReleaseTerminalResourcesAsync(claim.ExecutionId, default));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(partial));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(partial + ".retained"));
    }

    [Fact]
    public async Task OutputBytesRequireRunningOwnerAndRemainUnpublishedAfterFlushAndHash()
    {
        var store = new MemoryJournal();
        using var broker = Create(store);
        var (fixture, output, claim) = await PrepareOutput(broker);
        using var ownedOutput = output;
        byte[] bytes = [255, 216, 255, 1, 2, 3, 4, 255, 217];
        var chunk = Chunk(claim, output, bytes);
        Assert.False((await broker.WriteOutputAsync("session_test", chunk, CancellationToken.None)).IsSuccess);
        Assert.Equal(new BridgeExecutionBroker.BindingFault("output_write", "ownership_or_state", "authorized"), broker.LastBindingFault);
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, "outputs"), "*.part", SearchOption.AllDirectories));
        Assert.True((await broker.ReportAsync("session_test", Report(claim, 2, "page_started", "running", "page_01"), CancellationToken.None)).IsSuccess);
        var first = chunk with { Count = 4, Data = BridgeAdapterCryptography.Encode(bytes.AsSpan(0, 4)), Sha256 = Hash(bytes.AsSpan(0, 4)) };
        Assert.Equal(4, (await broker.WriteOutputAsync("session_test", first, CancellationToken.None)).Value!.NextOffset);
        Assert.False((await broker.WriteOutputAsync("session_test", first, CancellationToken.None)).IsSuccess);
        Assert.Equal(new BridgeExecutionBroker.BindingFault("output_write", "offset", "running"), broker.LastBindingFault);
        var final = chunk with { Offset = 4, Count = 5, Data = BridgeAdapterCryptography.Encode(bytes.AsSpan(4)), Sha256 = Hash(bytes.AsSpan(4)) };
        Assert.Equal(9, (await broker.WriteOutputAsync("session_test", final, CancellationToken.None)).Value!.NextOffset);
        var sealedBytes = await broker.SealOutputAsync("session_test", Seal(chunk), CancellationToken.None);
        Assert.True(sealedBytes.Value!.Sealed);
        var file = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, "outputs"), "*.part", SearchOption.AllDirectories));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
        Assert.Throws<IOException>(() => File.Open(file, FileMode.Open, FileAccess.Write));
        await Assert.ThrowsAsync<InvalidDataException>(() => broker.ValidateOutputAsync(claim.ExecutionId,
            "template.psd", new UnexpectedInspection(), CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "outputs", output.Binding.ArtifactSetId)));
        Assert.True((await broker.ReportAsync("session_test", Report(claim, 3, "page_completed", "running", "page_01"), CancellationToken.None)).IsSuccess);
        Assert.False((await broker.ReportAsync("session_test", Report(claim, 4, "execution_completed", "completed"), CancellationToken.None)).IsSuccess);
        Assert.Equal("running", (await broker.QueryAsync(claim.ExecutionId, CancellationToken.None))!.State);
        // A sealed byte receipt plus page_completed must not bypass the independent
        // structure gate when a verification PSD is absent.
        await Assert.ThrowsAsync<InvalidDataException>(() => broker.ValidateOutputAsync(claim.ExecutionId,
            "template.psd", new UnexpectedInspection(), CancellationToken.None));
        await broker.CancelAsync(claim.ExecutionId, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => broker.ValidateOutputAsync(claim.ExecutionId,
            "template.psd", new UnexpectedInspection(), CancellationToken.None));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
        using var unlocked = File.Open(file, FileMode.Open, FileAccess.Write);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("claim")]
    [InlineData("digest")]
    [InlineData("handle")]
    [InlineData("artifact")]
    [InlineData("psd_disabled")]
    [InlineData("verification_not_negotiated")]
    [InlineData("path")]
    [InlineData("offset")]
    [InlineData("length")]
    [InlineData("hash")]
    [InlineData("noncanonical")]
    [InlineData("cancelled")]
    public async Task OutputTransferMismatchNeverWritesOutsideItsCurrentPage(string mutation)
    {
        using var broker = Create(new MemoryJournal());
        var (fixture, output, claim) = await PrepareOutput(broker);
        using var ownedOutput = output;
        await broker.ReportAsync("session_test", Report(claim, 2, "page_started", "running", "page_01"), CancellationToken.None);
        var sourceHashes = fixture.Hashes();
        var sentinel = Path.Combine(fixture.Root, "outputs", "unrelated.txt");
        await File.WriteAllTextAsync(sentinel, "existing file");
        var chunk = Chunk(claim, output, [255, 216, 255, 0]);
        chunk = mutation switch
        {
            "claim" => chunk with { ClaimId = "claim_wrong" },
            "digest" => chunk with { PlanDigest = "sha256:" + new string('0', 64) },
            "handle" => chunk with { OutputRootHandle = new string('A', 43) },
            "artifact" => chunk with { ArtifactSetId = "other" },
            "psd_disabled" => chunk with { OutputKind = "editable_psd" },
            "verification_not_negotiated" => chunk with { OutputKind = "verification_psd" },
            "path" => chunk with { OutputKind = "../unrelated.txt" },
            "offset" => chunk with { Offset = 1 },
            "length" => chunk with { ByteLength = 2147483649 },
            "hash" => chunk with { Sha256 = new string('0', 64) },
            "noncanonical" => chunk with { Data = chunk.Data[..^1] + "B" },
            _ => chunk
        };
        if (mutation == "cancelled") await broker.CancelAsync(claim.ExecutionId, CancellationToken.None);
        Assert.False((await broker.WriteOutputAsync(mutation == "session" ? "wrong" : "session_test", chunk, CancellationToken.None)).IsSuccess);
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, "outputs"), "*.part", SearchOption.AllDirectories));
        Assert.Equal("existing file", await File.ReadAllTextAsync(sentinel));
        Assert.Equal(sourceHashes, fixture.Hashes());
    }

    [Fact]
    public async Task WholeOutputHashMismatchPoisonsOnlyTheUnpublishedReservation()
    {
        using var broker = Create(new MemoryJournal());
        var (_, output, claim) = await PrepareOutput(broker);
        using var ownedOutput = output;
        await broker.ReportAsync("session_test", Report(claim, 2, "page_started", "running", "page_01"), CancellationToken.None);
        var chunk = Chunk(claim, output, [255, 216, 255, 0]);
        Assert.True((await broker.WriteOutputAsync("session_test", chunk, CancellationToken.None)).IsSuccess);
        Assert.False((await broker.SealOutputAsync("session_test", Seal(chunk) with { Sha256 = new string('0', 64) }, CancellationToken.None)).IsSuccess);
        Assert.False((await broker.SealOutputAsync("session_test", Seal(chunk), CancellationToken.None)).IsSuccess);
        await broker.DisconnectAsync(CancellationToken.None);
        Assert.Equal("unknown", (await broker.QueryAsync(claim.ExecutionId, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task JpgOnlyCannotSubstituteAnEditablePsdReceipt()
    {
        using var broker = Create(new MemoryJournal());
        var (fixture, output, claim) = await PrepareOutput(broker);
        await broker.ReportAsync("session_test", Report(claim, 2, "page_started", "running", "page_01"), default);
        var chunk = Chunk(claim, output, [56, 66, 80, 83, 0, 1, 0, 0]) with
        {
            OutputKind = "editable_psd"
        };
        Assert.False((await broker.WriteOutputAsync("session_test", chunk, default)).IsSuccess);
        Assert.False((await broker.SealOutputAsync("session_test", Seal(chunk), default)).IsSuccess);
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, "outputs"), "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task IndependentWorkerNeverBlocksClaimsOrCancellationAndCannotValidateAfterCancellation()
    {
        using var broker = Create(new MemoryJournal());
        var (fixture, output, claim) = await PrepareOutput(broker, includePsd: true);
        using var ownedOutput = output;
        await broker.ReportAsync("session_test", Report(claim, 2, "page_started", "running", "page_01"), CancellationToken.None);
        foreach (var chunk in new[] {
            Chunk(claim, output, RenderJpegValidatorTests.Jpeg(width: 790, height: 1252)),
            Chunk(claim, output, [56, 66, 80, 83, 0, 1, 0, 0]) with { OutputKind = "editable_psd" } })
        {
            Assert.True((await broker.WriteOutputAsync("session_test", chunk, CancellationToken.None)).IsSuccess);
            Assert.True((await broker.SealOutputAsync("session_test", Seal(chunk), CancellationToken.None)).IsSuccess);
        }
        await broker.ReportAsync("session_test", Report(claim, 3, "page_completed", "running", "page_01"), CancellationToken.None);
        var worker = new BlockingInspection();
        var validation = broker.ValidateOutputAsync(claim.ExecutionId, fixture.Sources[0].OwnedRelativePath, worker, CancellationToken.None);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            Assert.True((await broker.ClaimAsync("session_test", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2))).IsSuccess);
            await broker.CancelAsync(claim.ExecutionId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { worker.Complete.TrySetResult(PsdInspectionResult.Failure(new("TPL_PSD_WORKER_FAILED", "failed", "retry", false))); }
        await Assert.ThrowsAsync<InvalidDataException>(() => validation);
        Assert.True((await broker.QueryAsync(claim.ExecutionId, CancellationToken.None))!.CancelRequested);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "outputs", output.Binding.ArtifactSetId)));
    }

    private async Task<(BridgeResourceLeaseTests.Fixture, BridgeOutputReservation, AdapterClaim)> PrepareOutput(BridgeExecutionBroker broker,
        bool includePsd = false, bool verificationPsd = false, bool sizeTable = false, bool sizeInsertion = false, int attempt = 1)
    {
        await broker.InitializeAsync(CancellationToken.None);
        var fixture = new BridgeResourceLeaseTests.Fixture();
        if (includePsd) fixture.Plan["output"]!["editable_psd"] = JsonNode.Parse("""{"enabled":true,"file_name":"01-detail.psd"}""");
        fixture.Plan["required_capabilities"]!.AsArray().Add(RenderPlanPlacementMigration.Capability);
        fixture.Plan["targets"]![0]!["layout"]!["placement"] = JsonNode.Parse("""
            {"coordinate_space":"document_pixels","source_width":4000,"source_height":6000,
             "left":-5,"top":10,"width":800,"height":1200,"clipping":"preserve_template_masks_and_canvas"}
            """);
        fixture.Rehash();
        if (sizeTable)
        {
            fixture.Plan["document"]!["content_type"] = "dynamic_size_table";
            fixture.Plan["required_capabilities"]!.AsArray().Add(RenderPlanPlacementMigration.SizeTableCapability);
            var resource = fixture.Plan["resources"]![1]!;
            byte[] png = [137, 80, 78, 71, 13, 10, 26, 10];
            File.WriteAllBytes(Path.Combine(fixture.Root, fixture.Sources[1].OwnedRelativePath), png);
            resource["role"] = "deterministic_size_table";
            resource["media_type"] = "image/png";
            resource["sha256"] = Hash(png);
            resource["byte_length"] = png.Length;
            fixture.Sources[1] = fixture.Sources[1] with { Purpose = "deterministic_size_table", MediaType = "image/png" };
            fixture.Plan["targets"]![0]!["layout"]!["intent"] = "size_table_complete";
            fixture.Plan["targets"]![0]!["layout"]!["placement"] = JsonNode.Parse("""
                {"coordinate_space":"document_pixels","source_width":1000,"source_height":400,
                 "left":10,"top":20,"width":750,"height":300,"clipping":"preserve_template_masks_and_canvas"}
                """);
            fixture.Plan["size_table"] = new JsonObject
            {
                ["size_table_id"] = "size_commit_01", ["grid_digest"] = "sha256:" + Hash(png),
                ["style_version"] = "style_v1", ["layout_version"] = "grid_v1", ["embed_as"] = "smart_object",
                ["render_resource_id"] = resource["resource_id"]!.GetValue<string>(),
                ["structured_data_digest"] = "sha256:" + new string('c', 64),
                ["columns"] = JsonNode.Parse("""[{"column_id":"col_1","label":"尺码","index":0}]"""),
                ["rows"] = JsonNode.Parse("""[{"row_id":"row_1","index":0,"cells":{"col_1":"L"}}]""")
            };
            if (sizeInsertion)
            {
                var target = fixture.Plan["targets"]![0]!;
                var anchor = new JsonObject { ["layer_path"] = new JsonArray(2), ["layer_path_order"] = "psd_bottom_to_top",
                    ["layer_id"] = 257, ["kind"] = "pixel", ["name"] = "old table",
                    ["structure_signature"] = target["expected_layer"]!["structure_signature"]!.DeepClone() };
                fixture.Plan["size_table"]!["insertion"] = new JsonObject {
                    ["layer_name"] = "new size table", ["above_layer"] = anchor,
                    ["hidden_layers"] = new JsonArray(anchor.DeepClone()),
                    ["safe_bounds"] = target["layout"]!["safe_bounds"]!.DeepClone(),
                    ["placement"] = target["layout"]!["placement"]!.DeepClone() };
                fixture.Plan["targets"] = new JsonArray();
                fixture.Plan["required_capabilities"]!.AsArray().Remove(
                    fixture.Plan["required_capabilities"]!.AsArray().Single(item => item!.GetValue<string>() == RenderPlanPlacementMigration.SizeTableCapability));
                fixture.Plan["required_capabilities"]!.AsArray().Add(RenderPlanPlacementMigration.SizeInsertionCapability);
            }
            fixture.Rehash();
        }
        if (attempt != 1)
        {
            fixture.Plan["plan_id"] = $"plan_attempt_{attempt}";
            fixture.Plan["idempotency_key"] = $"batch_page_attempt_{attempt}";
            fixture.Plan["document"]!["page_id"] = $"page_{attempt:00}";
            fixture.Plan["output"]!["artifact_set_id"] = $"artifact_page_{attempt}";
            fixture.Rehash();
        }
        _resources.Add(fixture);
        var output = BridgeOutputReservation.Create(fixture.Plan.ToJsonString(), "outputs", new LocalAppPathPolicy(fixture.Root),
            new RenderPlanValidator(new JsonSchemaContractValidator()));
        var lease = await fixture.Open();
        await broker.RegisterResourcesAsync(lease, CancellationToken.None, output);
        var request = JsonNode.Parse(Request(plan: fixture.Plan, bindings: lease.Bindings))!;
        request["body"]!["payload"]!["output_bindings"]![0]!["output_root_handle"] = output.Binding.OutputRootHandle;
        await broker.SubmitAsync(request.ToJsonString(), CancellationToken.None);
        var claim = (await broker.ClaimAsync("session_test", CancellationToken.None)).Value!.Claim!;
        await broker.AcknowledgeAsync("session_test", new(claim.ClaimId, claim.ExecutionId, claim.PlanDigest), CancellationToken.None);
        return (fixture, output, claim);
    }

    private static AdapterOutputWrite Chunk(AdapterClaim claim, BridgeOutputReservation output, byte[] bytes) =>
        new(claim.ClaimId, claim.ExecutionId, claim.PlanDigest, output.Binding.OutputRootHandle, output.Binding.ArtifactSetId,
            "jpg", bytes.Length, 0, bytes.Length, Hash(bytes), BridgeAdapterCryptography.Encode(bytes));
    private static AdapterOutputSeal Seal(AdapterOutputWrite chunk) => new(chunk.ClaimId, chunk.ExecutionId, chunk.PlanDigest,
        chunk.OutputRootHandle, chunk.ArtifactSetId, chunk.OutputKind, chunk.ByteLength, chunk.Sha256);
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class UnexpectedInspection : IPsdInspectionPort
    {
        public Task<PsdInspectionResult> InspectAsync(PsdInspectionRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Inspection must not run before execution and artifact gates");
    }

    private sealed class BlockingInspection : IPsdInspectionPort
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<PsdInspectionResult> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PsdInspectionResult> InspectAsync(PsdInspectionRequest request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return Complete.Task;
        }
    }
}
