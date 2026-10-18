using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Application.Rendering;
using Desktop.Contracts.Validation;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Tests;

public sealed class RenderStyleOutputRegistryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExactMultiStyleTargetsPagesAndDirectoryOwnershipSurviveReopen(bool unified, bool psd)
    {
        using var fixture = new Fixture();
        string saved;
        string initialJournal;
        RenderStyleOutputStage[] stages;
        await using (var store = await fixture.Open())
        {
            var batch = await fixture.Seed(store, unified, psd);
            initialJournal = await fixture.Scalar("SELECT payload_json FROM render_batch_journals;");
            using var prepared = await new RenderStyleOutputRegistry(store).PrepareAsync(batch.BatchId, default);
            Assert.Equal(2, prepared.Registration.Styles.Length);
            Assert.Equal(2, prepared.Stages.Count);
            Assert.Equal(3, prepared.Registration.Styles.Sum(style => style.Pages.Length));
            Assert.Single(prepared.Registration.Styles.SelectMany(style => style.Pages), page => page.PlanDigest is null);
            foreach (var style in prepared.Registration.Styles)
            {
                var target = style.Target;
                Assert.Equal(Path.Combine(fixture.Input, target.StyleId == "style_a" ? "实际A" : "实际B"), target.SourceDirectory);
                Assert.Equal(unified ? fixture.Public : target.SourceDirectory, target.ParentDirectory);
                Assert.False(Directory.Exists(target.FinalDirectory));
                Assert.Empty(Directory.EnumerateFileSystemEntries(target.StagingDirectory));
                Assert.Throws<IOException>(() => Directory.Move(target.StagingDirectory, target.StagingDirectory + "-swapped"));
                Assert.Throws<IOException>(() => Directory.Move(target.SourceDirectory, target.SourceDirectory + "-swapped"));
                Assert.Equal(psd, style.Pages.First(page => page.PlanDigest is not null).PsdName is not null);
                Assert.Equal("source sentinel", File.ReadAllText(Path.Combine(target.SourceDirectory, "source.jpg")));
            }
            saved = await fixture.Scalar("SELECT payload_json FROM render_output_registrations;");
            stages = prepared.Stages.OrderBy(stage => stage.StyleId).ToArray();
            Assert.False(Directory.Exists(fixture.Evidence));
        }
        await using (var reopened = await fixture.Open())
        {
            using var prepared = await new RenderStyleOutputRegistry(reopened).PrepareAsync("batch_test", default);
            Assert.Equal(stages, prepared.Stages.OrderBy(stage => stage.StyleId));
            Assert.Equal(saved, await fixture.Scalar("SELECT payload_json FROM render_output_registrations;"));
            Assert.Equal(initialJournal, await fixture.Scalar("SELECT payload_json FROM render_batch_journals;"));
            Assert.Equal("0", await fixture.Scalar("SELECT count(*) FROM render_page_checkpoints;"));
            Assert.Equal("0", await fixture.Scalar("SELECT count(*) FROM render_output_registrations WHERE render_revision<>1;"));
        }
    }

    [Fact]
    public async Task NativeCreateIsExclusiveAndPinsTheExactCreatedDirectory()
    {
        using var fixture = new Fixture();
        await using var store = await fixture.Open();
        var batch = await fixture.Seed(store);
        var target = RenderStyleOutputPlanner.Plan(batch)[0];
        using var parent = RenderOutputDirectoryLease.Open(target.ParentDirectory, true);
        RenderOutputDirectoryIdentity identity;
        using (var stage = parent.CreateStagingChild(Path.GetFileName(target.StagingDirectory)))
        {
            identity = stage.Identity;
            Assert.Equal(parent.Identity.Volume, identity.Volume);
            Assert.Throws<IOException>(() => Directory.Move(stage.Path, stage.Path + "-moved"));
            Assert.Equal("RENDER_OUTPUT_DIRECTORY_CONFLICT", Assert.Throws<RenderOutputTargetException>(() =>
                parent.CreateStagingChild(Path.GetFileName(target.StagingDirectory))).Code);
        }
        using var readBack = RenderOutputDirectoryLease.Open(target.StagingDirectory);
        Assert.Equal(identity, readBack.Identity);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target.StagingDirectory));
    }

    [Theory]
    [InlineData("target")]
    [InlineData("source")]
    [InlineData("identity")]
    [InlineData("page")]
    [InlineData("name")]
    [InlineData("manifest")]
    public async Task FrozenRegistrationCannotBeReplacedEvenBeforeAnyStage(string mutation)
    {
        using var fixture = new Fixture();
        await using var store = await fixture.Open();
        var batch = await fixture.Seed(store);
        using var preflight = RenderStyleOutputPlanner.Preflight(batch, default);
        var original = RenderStyleOutputRegistrationData.Capture(batch, (await store.LoadRenderBatchAsync(batch.BatchId, default))!, preflight);
        await store.RegisterRenderOutputsAsync(original, default);
        await store.RegisterRenderOutputsAsync(original, default); // Exact replay is idempotent.
        var style = original.Styles[0];
        var changed = mutation switch
        {
            "target" => style with { Target = style.Target with { FinalDirectory = style.Target.FinalDirectory + "-other" } },
            "source" => style with { Target = style.Target with { SourceDirectory = fixture.Input } },
            "identity" => style with { SourceIdentity = style.SourceIdentity with { Created = style.SourceIdentity.Created + 1 } },
            "page" => style with { Pages = style.Pages.RemoveAt(0) },
            "name" => style with { Pages = style.Pages.SetItem(0, style.Pages[0] with { JpegName = "other.jpg" }) },
            _ => style
        };
        var candidate = original with { Styles = original.Styles.SetItem(0, changed),
            InputManifestDigest = mutation == "manifest" ? "sha256:" + new string('f', 64) : original.InputManifestDigest };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RegisterRenderOutputsAsync(candidate, default));
        Assert.Equal(RenderStyleOutputRegistrationData.Encode(original), await fixture.Scalar("SELECT payload_json FROM render_output_registrations;"));
        Assert.All(preflight.Targets, target => Assert.False(Directory.Exists(target.StagingDirectory)));
    }

    [Fact]
    public async Task RegistrationTransactionFailureCreatesNoDirectoryOrPartialRecord()
    {
        using var fixture = new Fixture();
        await using var store = await fixture.Open();
        var batch = await fixture.Seed(store);
        await fixture.Sql("CREATE TRIGGER fail_registration BEFORE INSERT ON render_output_registrations BEGIN SELECT RAISE(ABORT,'TEST_FAIL'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => new RenderStyleOutputRegistry(store).PrepareAsync(batch.BatchId, default));
        Assert.Equal("0", await fixture.Scalar("SELECT count(*) FROM render_output_registrations;"));
        Assert.Equal("0", await fixture.Scalar("SELECT count(*) FROM render_output_stages;"));
        Assert.All(RenderStyleOutputPlanner.Plan(batch), target => Assert.False(Directory.Exists(target.StagingDirectory)));
        await fixture.Sql("DROP TRIGGER fail_registration;");
        using var prepared = await new RenderStyleOutputRegistry(store).PrepareAsync(batch.BatchId, default);
        Assert.Equal(2, prepared.Stages.Count);
    }

    [Fact]
    public async Task CrashAfterIntentButBeforeDirectoryCreationResumesTheSameTargets()
    {
        using var fixture = new Fixture();
        await using (var store = await fixture.Open())
        {
            var batch = await fixture.Seed(store);
            using var preflight = RenderStyleOutputPlanner.Preflight(batch, default);
            var registration = RenderStyleOutputRegistrationData.Capture(batch, (await store.LoadRenderBatchAsync(batch.BatchId, default))!, preflight);
            await store.RegisterRenderOutputsAsync(registration, default);
        }
        var before = await fixture.Scalar("SELECT payload_json FROM render_output_registrations;");
        await using var reopened = await fixture.Open();
        using var prepared = await new RenderStyleOutputRegistry(reopened).PrepareAsync("batch_test", default);
        Assert.Equal(2, prepared.Stages.Count);
        Assert.Equal(before, await fixture.Scalar("SELECT payload_json FROM render_output_registrations;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DatabaseFailureAfterCreationPreservesUnregisteredDirectoryAndNeverAdoptsIt(bool second)
    {
        using var fixture = new Fixture();
        await using var store = await fixture.Open();
        var batch = await fixture.Seed(store);
        await fixture.Sql("CREATE TRIGGER fail_stage BEFORE INSERT ON render_output_stages "
            + (second ? "WHEN NEW.style_id='style_b' " : "") + "BEGIN SELECT RAISE(ABORT,'TEST_FAIL'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => new RenderStyleOutputRegistry(store).PrepareAsync(batch.BatchId, default));
        Assert.Equal(second ? "1" : "0", await fixture.Scalar("SELECT count(*) FROM render_output_stages;"));
        var target = RenderStyleOutputPlanner.Plan(batch)[second ? 1 : 0];
        Assert.True(Directory.Exists(target.StagingDirectory));
        using (var lease = RenderOutputDirectoryLease.Open(target.StagingDirectory))
        {
            await fixture.Sql("DROP TRIGGER fail_stage;");
            var error = await Assert.ThrowsAsync<RenderOutputTargetException>(() =>
                new RenderStyleOutputRegistry(store).PrepareAsync(batch.BatchId, default));
            Assert.Equal("RENDER_OUTPUT_DIRECTORY_CONFLICT", error.Code);
            lease.Verify();
        }
        Assert.False(Directory.Exists(target.FinalDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target.StagingDirectory));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("parent")]
    [InlineData("stage")]
    public async Task ChangedDirectoryIdentityIsNotRecoveredByMatchingPath(string part)
    {
        using var fixture = new Fixture();
        await using var store = await fixture.Open();
        var batch = await fixture.Seed(store, unified: true);
        RenderStyleOutputTarget target;
        using (var prepared = await new RenderStyleOutputRegistry(store).PrepareAsync(batch.BatchId, default))
            target = prepared.Registration.Styles[0].Target;
        var original = part switch { "source" => target.SourceDirectory, "parent" => target.ParentDirectory, _ => target.StagingDirectory };
        Directory.Move(original, original + "-original");
        Directory.CreateDirectory(original);
        var before = await fixture.Scalar("SELECT group_concat(payload_json,'|') FROM render_output_stages;");
        var error = await Assert.ThrowsAsync<RenderOutputTargetException>(() => new RenderStyleOutputRegistry(store).PrepareAsync(batch.BatchId, default));
        Assert.Equal("RENDER_OUTPUT_DIRECTORY_IDENTITY_MISMATCH", error.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(original));
        Assert.Equal(before, await fixture.Scalar("SELECT group_concat(payload_json,'|') FROM render_output_stages;"));
    }

    [Theory]
    [InlineData("missing-row")]
    [InlineData("missing-directory")]
    [InlineData("marker-only")]
    [InlineData("final-conflict")]
    public async Task IncompleteOrUnregisteredEvidenceIsPreservedNotRebuiltOrAdopted(string failure)
    {
        using var fixture = new Fixture();
        await using var store = await fixture.Open();
        var batch = await fixture.Seed(store);
        RenderStyleOutputTarget target;
        using (var prepared = await new RenderStyleOutputRegistry(store).PrepareAsync(batch.BatchId, default))
            target = prepared.Registration.Styles[0].Target;
        if (failure == "missing-row") await fixture.Sql("DELETE FROM render_output_stages WHERE style_id='style_a';");
        if (failure == "missing-directory") Directory.Move(target.StagingDirectory, target.StagingDirectory + "-original");
        if (failure == "marker-only") File.WriteAllText(Path.Combine(target.StagingDirectory, "committed.marker"), batch.BatchId);
        if (failure == "final-conflict") Directory.CreateDirectory(target.FinalDirectory);
        var before = fixture.Inventory();
        if (failure == "marker-only")
            Assert.Equal("RENDER_OUTPUT_REGISTRATION_INVALID", (await Assert.ThrowsAsync<InvalidDataException>(() =>
                new RenderStyleOutputRegistry(store).PrepareAsync(batch.BatchId, default))).Message);
        else
            await Assert.ThrowsAsync<RenderOutputTargetException>(() => new RenderStyleOutputRegistry(store).PrepareAsync(batch.BatchId, default));
        Assert.Equal(before, fixture.Inventory());
        if (failure == "missing-directory") Assert.False(Directory.Exists(target.StagingDirectory));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("version")]
    [InlineData("unknown-field")]
    [InlineData("duplicate-field")]
    [InlineData("missing-field")]
    [InlineData("directory")]
    [InlineData("page")]
    [InlineData("source-identity")]
    [InlineData("input-manifest")]
    public async Task CorruptionWithRecomputedHashStillCannotChangeFrozenFacts(string corruption)
    {
        using var fixture = new Fixture();
        await using var store = await fixture.Open();
        await fixture.Seed(store);
        using (await new RenderStyleOutputRegistry(store).PrepareAsync("batch_test", default)) { }
        var json = await fixture.Scalar("SELECT payload_json FROM render_output_registrations;");
        var node = JsonNode.Parse(json)!;
        switch (corruption)
        {
            case "version": node["Version"] = 2; break;
            case "unknown-field": node["Unknown"] = true; break;
            case "missing-field": node.AsObject().Remove("InputManifestDigest"); break;
            case "directory": node["Styles"]![0]!["Target"]!["FinalDirectory"] = fixture.Input; break;
            case "page": node["Styles"]![0]!["Pages"]!.AsArray().RemoveAt(0); break;
            case "source-identity": node["Styles"]![0]!["SourceIdentity"]!["Created"] = 0; break;
            case "input-manifest": await fixture.Sql("UPDATE input_batches SET manifest_digest='sha256:" + new string('f', 64) + "';"); break;
        }
        json = node.ToJsonString();
        if (corruption == "duplicate-field") json = json.Insert(1, "\"Version\":1,");
        await fixture.ReplacePayload("render_output_registrations", json, corruption == "hash" ? new string('0', 64) : RenderStyleOutputRegistrationData.Hash(json));
        var before = fixture.Inventory();
        await Assert.ThrowsAsync<InvalidDataException>(() => new RenderStyleOutputRegistry(store).PrepareAsync("batch_test", default));
        Assert.Equal(before, fixture.Inventory());
        Assert.Equal(json, await fixture.Scalar("SELECT payload_json FROM render_output_registrations;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyOrUnknownCannotAcquireNewPublicOwnership(bool unknown)
    {
        using var fixture = new Fixture();
        await using var store = await fixture.Open();
        await fixture.Seed(store, legacy: !unknown);
        if (unknown)
        {
            var journal = (await store.LoadRenderBatchAsync("batch_test", default))!;
            var pages = journal.Pages.ToArray();
            pages[0] = pages[0] with { Status = RenderBatchPageStatus.Unknown, ExecutionId = "execution_unknown", ReasonCode = "RENDER_RECOVERY_REQUIRED" };
            await store.SaveRenderBatchAsync(journal with { Revision = 2, Status = RenderBatchStatus.RecoveryRequired, Pages = pages }, 1, default);
        }
        var before = await fixture.Scalar("SELECT group_concat(payload_json,'|') FROM render_batch_journals;");
        await Assert.ThrowsAsync<InvalidDataException>(() => new RenderStyleOutputRegistry(store).PrepareAsync("batch_test", default));
        Assert.Equal(before, await fixture.Scalar("SELECT group_concat(payload_json,'|') FROM render_batch_journals;"));
        Assert.Equal("0", await fixture.Scalar("SELECT count(*) FROM render_output_registrations;"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Public));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("style")]
    [InlineData("unknown-field")]
    [InlineData("duplicate-field")]
    [InlineData("missing-identity")]
    [InlineData("changed-identity")]
    public async Task StageRecordCorruptionNeverAuthorizesAPath(string corruption)
    {
        using var fixture = new Fixture();
        await using var store = await fixture.Open();
        await fixture.Seed(store);
        using (await new RenderStyleOutputRegistry(store).PrepareAsync("batch_test", default)) { }
        var node = JsonNode.Parse(await fixture.Scalar("SELECT payload_json FROM render_output_stages WHERE style_id='style_a';"))!;
        if (corruption == "style") node["StyleId"] = "other";
        if (corruption == "unknown-field") node["Unknown"] = true;
        if (corruption == "missing-identity") node.AsObject().Remove("Identity");
        if (corruption == "changed-identity") node["Identity"]!["Created"] = node["Identity"]!["Created"]!.GetValue<long>() + 1;
        var json = node.ToJsonString();
        if (corruption == "duplicate-field") json = json.Insert(1, "\"BatchId\":\"batch_test\",");
        await fixture.ReplacePayload("render_output_stages", json,
            corruption == "hash" ? new string('0', 64) : RenderStyleOutputRegistrationData.Hash(json));
        var before = fixture.Inventory();
        if (corruption == "changed-identity")
            Assert.Equal("RENDER_OUTPUT_DIRECTORY_IDENTITY_MISMATCH", (await Assert.ThrowsAsync<RenderOutputTargetException>(() =>
                new RenderStyleOutputRegistry(store).PrepareAsync("batch_test", default))).Code);
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => new RenderStyleOutputRegistry(store).PrepareAsync("batch_test", default));
        Assert.Equal(before, fixture.Inventory());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDoesNotCreateOwnershipOrModifyExistingRegistration(bool registered)
    {
        using var fixture = new Fixture();
        await using var store = await fixture.Open();
        await fixture.Seed(store);
        if (registered) { using var ready = await new RenderStyleOutputRegistry(store).PrepareAsync("batch_test", default); }
        var before = fixture.Inventory();
        var rows = await fixture.Scalar("SELECT group_concat(payload_json,'|') FROM render_output_stages;");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new RenderStyleOutputRegistry(store).PrepareAsync("batch_test", cancellation.Token));
        Assert.Equal(before, fixture.Inventory());
        Assert.Equal(rows, await fixture.Scalar("SELECT group_concat(payload_json,'|') FROM render_output_stages;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V20MigrationPreservesV19FactsAndRollsBackBothNewTables(bool fail)
    {
        using var fixture = new Fixture();
        string input;
        await using (var store = await fixture.Open())
        {
            await fixture.Seed(store);
            input = JsonSerializer.Serialize(await store.GetAsync("batch_test", default));
        }
        await fixture.Sql(LegacyMigrationFixture.RemoveTpl02Schema + "DROP TABLE render_output_publication_files; DROP TABLE render_output_publications; DROP TABLE render_output_stages; DROP TABLE render_output_registrations; DELETE FROM schema_migrations WHERE version>=20;"
            + (fail ? "CREATE TRIGGER fail_v20 BEFORE INSERT ON schema_migrations WHEN NEW.version=20 BEGIN SELECT RAISE(ABORT,'TEST_FAIL'); END;" : ""));
        var migrations = await fixture.Scalar("SELECT group_concat(version||checksum||applied_at,'|') FROM (SELECT * FROM schema_migrations ORDER BY version);");
        await using var reopened = new SqliteDesktopStore(fixture.Options);
        if (fail) await Assert.ThrowsAsync<SqliteException>(() => reopened.InitializeAsync());
        else
        {
            await reopened.InitializeAsync();
            Assert.Equal(input, JsonSerializer.Serialize(await reopened.GetAsync("batch_test", default)));
        }
        Assert.Equal(migrations, await fixture.Scalar("SELECT group_concat(version||checksum||applied_at,'|') FROM (SELECT * FROM schema_migrations WHERE version<=19 ORDER BY version);"));
        Assert.Equal(fail ? "19" : "29", await fixture.Scalar("SELECT max(version) FROM schema_migrations;"));
        Assert.Equal(fail ? "0" : "2", await fixture.Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('render_output_registrations','render_output_stages');"));
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly TestDirectory _directory = new();
        internal DesktopDataOptions Options { get; }
        internal string Input { get; }
        internal string Public { get; }
        internal string Evidence { get; }
        internal Fixture()
        {
            Input = Directory.CreateDirectory(Path.Combine(_directory.Path, "精确源目录")).FullName;
            Public = Directory.CreateDirectory(Path.Combine(_directory.Path, "public")).FullName;
            Evidence = Path.Combine(_directory.Path, "private-evidence");
            Options = new(Path.Combine(_directory.Path, "app"));
        }
        internal async Task<SqliteDesktopStore> Open()
        {
            var store = new SqliteDesktopStore(Options);
            await store.InitializeAsync();
            return store;
        }
        internal async Task<BatchDraft> Seed(SqliteDesktopStore store, bool unified = false, bool psd = false, bool legacy = false)
        {
            var now = DateTimeOffset.UtcNow;
            var group = TemplateGroup.Create("group", "fixture", now);
            var page = TemplatePage.Create("page_1", "revision", 0, "page", TemplatePageContentType.NormalComposition,
                TemplateGenerationMode.OncePerProduct, null, true, "resource");
            await store.SaveGroupAsync(group, default);
            await store.SaveDraftRevisionAsync(TemplateRevision.CreateDraft("revision", group.GroupId, 1, now, [page]), default);
            var snapshot = BatchTemplateSnapshot.Restore("snapshot", "batch_test", "revision",
                [new("page_1", 0, TemplatePageContentType.NormalComposition, TemplateGenerationMode.OncePerProduct, null, "resource", [])],
                "sha256:" + new string('a', 64), now);
            var styles = new[] { StyleDraft.Create("style_a", "同名款", "实际A", 0, ["asset_a"]),
                StyleDraft.Create("style_b", "同名款", "实际B", 1, ["asset_b"]) };
            foreach (var style in styles)
            {
                var source = Directory.CreateDirectory(Path.Combine(Input, style.SourceDirectoryRelativePath)).FullName;
                File.WriteAllText(Path.Combine(source, "source.jpg"), "source sentinel");
            }
            var assets = styles.Select(style => SourceAssetMetadata.CreateReadable(style.AssetIds[0], style.StyleId,
                style.SourceDirectoryRelativePath + "/source.jpg", "source.jpg", 128, new string('b', 64), 10, 10,
                SourceImageFormat.Jpeg, "image/jpeg")).ToArray();
            var batch = BatchDraft.Create("batch_test", Input, Evidence, snapshot, BatchSelectionMode.Automatic, psd, now,
                legacy ? null : unified ? BatchOutputSelection.Create(BatchOutputMode.UnifiedDirectory, Public)
                    : BatchOutputSelection.Create(BatchOutputMode.StyleSourceDirectories))
                .BeginScanning(now).MarkScanReady(BatchInputType.StyleDirectoryBatch, styles, assets,
                    "batches/batch_test/manifest.json", "sha256:" + new string('c', 64), now);
            await store.SaveAsync(batch, default);
            var pages = styles.Select((style, index) =>
            {
                var plan = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "render-plan-v1.valid.example.json")))!;
                plan["batch_id"] = batch.BatchId; plan["product_id"] = style.StyleId;
                plan["document"]!["page_id"] = "page_1";
                plan["plan_id"] = "plan_" + index; plan["idempotency_key"] = "render_key_" + index;
                plan["output"]!["artifact_set_id"] = "artifact_" + index;
                plan["output"]!["editable_psd"]!["enabled"] = psd;
                plan["output"]!["editable_psd"]!["file_name"] = psd ? "01-detail.psd" : null;
                plan["plan_digest"] = RenderPlanDigest.Compute(plan.ToJsonString());
                return new RenderBatchPage(style.StyleId, "page_1", null, 1, plan.ToJsonString(), RenderBatchPageStatus.Planned,
                    "AI_RENDER_PLAN_VALID", PreparationReasonCode: "AI_RENDER_PLAN_VALID");
            }).Append(new("style_a", "page_blocked", null, 1, null, RenderBatchPageStatus.Blocked,
                "AI_INPUT_MISSING", PreparationReasonCode: "AI_INPUT_MISSING")).ToArray();
            await store.SaveRenderBatchAsync(new(1, batch.BatchId, 1, RenderBatchStatus.Prepared, false, false, pages), 0, default);
            return batch;
        }
        internal Task<string> Sql(string sql) => Execute(sql, scalar: false);
        internal Task<string> Scalar(string sql) => Execute(sql, scalar: true);
        private async Task<string> Execute(string sql, bool scalar)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Options.DatabasePath, Pooling = false }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = sql;
            if (scalar) return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
            await command.ExecuteNonQueryAsync(); return "";
        }
        internal async Task ReplacePayload(string table, string json, string hash)
        {
            Assert.True(table is "render_output_registrations" or "render_output_stages");
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Options.DatabasePath, Pooling = false }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE {table} SET payload_json=$json,payload_sha256=$hash;";
            command.Parameters.AddWithValue("$json", json); command.Parameters.AddWithValue("$hash", hash);
            await command.ExecuteNonQueryAsync();
        }
        internal string[] Inventory() => Directory.EnumerateFileSystemEntries(Input, "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFileSystemEntries(Public, "*", SearchOption.AllDirectories)).Order(StringComparer.Ordinal).ToArray();
        public void Dispose() => _directory.Dispose();
    }
}
