using System.Buffers.Binary;
using System.Security.Cryptography;
using Desktop.Application.Inputs;
using Desktop.Contracts.Validation;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Inputs;
using Desktop.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Tests;

public sealed class BatchInputInfrastructureTests
{
    private const string RuleDigest =
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("legacy")]
    [InlineData("source")]
    [InlineData("unified")]
    public async Task WorkflowFacadeKeepsPublicChoiceSeparateFromPrivateEvidenceThroughSaveScanRestoreAndFreeze(string mode)
    {
        using var directory = new TestDirectory();
        var app = Path.Combine(directory.Path, "app");
        await using var store = new SqliteDesktopStore(new(app));
        var (service, repository, seed) = await OutputIntentScenario(directory.Path, store);
        var unified = Directory.CreateDirectory(Path.Combine(directory.Path, "public")).FullName;
        var choice = mode == "legacy" ? null : new BatchInputWorkflowPublicOutput(mode == "source"
            ? BatchInputWorkflowOutputMode.StyleSourceDirectories : BatchInputWorkflowOutputMode.UnifiedDirectory,
            mode == "source" ? null : unified);
        var command = new BatchInputWorkflowCommand(seed.BatchId, seed.TemplateGroupId, seed.InputRoot,
            choice is null ? seed.OutputRoot : "", seed.SelectedPageIds, BatchInputWorkflowSelectionMode.Automatic, false, choice);
        var facade = new BatchInputWorkflowFacade(service, new LocalAppPathPolicy(app));
        var draft = await facade.SaveDraftAsync(command, default);
        Assert.Equal(choice, draft.PublicOutput);
        if (choice is null) Assert.Equal(seed.OutputRoot, draft.OutputRoot);
        else
        {
            Assert.StartsWith(app + Path.DirectorySeparatorChar, draft.OutputRoot, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(unified, draft.OutputRoot);
            Assert.NotEqual(seed.InputRoot, draft.OutputRoot);
        }
        Assert.False(Directory.Exists(draft.OutputRoot));
        Assert.Equal(draft.OutputRoot, (await facade.SaveDraftAsync(command, default)).OutputRoot);
        var ready = await facade.ScanAsync(command, default);
        Assert.Equal(BatchInputWorkflowStatus.ScanReady, ready.Status);
        Assert.Equal(choice, ready.PublicOutput);
        Assert.Equal(draft.OutputRoot, ready.OutputRoot);
        var restored = Assert.Single(await new BatchInputWorkflowFacade(service, new LocalAppPathPolicy(app)).ListRecoverableAsync(default));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(ready), System.Text.Json.JsonSerializer.Serialize(restored));
        Assert.Equal(ready.OutputRoot, (await repository.GetAsync(seed.BatchId, default))!.OutputRoot);
        Assert.False(Directory.Exists(ready.OutputRoot));
        var changed = command with { OutputRoot = "", PublicOutput = new(BatchInputWorkflowOutputMode.UnifiedDirectory, unified + "-other") };
        await Assert.ThrowsAsync<InputRuleViolationException>(() => facade.SaveDraftAsync(changed, default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(unified));
    }

    [Theory]
    [InlineData("missing_db")]
    [InlineData("missing_journal")]
    [InlineData("different_db")]
    [InlineData("old_version")]
    [InlineData("missing_choice")]
    [InlineData("unknown_choice_field")]
    [InlineData("unknown_mode")]
    [InlineData("numeric_mode")]
    [InlineData("unexpected_root")]
    [InlineData("duplicate_mode")]
    [InlineData("duplicate_version")]
    [InlineData("unknown_top_field")]
    public async Task OutputIntentCorruptionNeverFallsBackToLegacyLayout(string corruption)
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(Path.Combine(directory.Path, "app"));
        await using var store = new SqliteDesktopStore(options);
        var (service, repository, command) = await OutputIntentScenario(directory.Path, store);
        var draft = await service.CreateDraftFromTemplateAsync(command);
        var ready = await service.ScanAsync(draft.BatchId);
        Assert.Equal(BatchScanStatus.ScanReady, ready.ScanStatus);
        var journalPath = Path.Combine(options.RootDirectory, "batches", draft.BatchId, "asset-manifest", "commit.journal.json");
        var original = await File.ReadAllTextAsync(journalPath);
        if (corruption == "missing_journal")
        {
            // Remove only this fixture's own journal, never a live application record.
            File.Delete(journalPath);
            Assert.Equal("INPUT_MANIFEST_DATABASE_MISMATCH", (await Assert.ThrowsAsync<InvalidOperationException>(
                () => repository.GetAsync(draft.BatchId, default))).Message);
            Assert.False(File.Exists(journalPath));
            return;
        }
        if (corruption.EndsWith("_db", StringComparison.Ordinal))
            await ExecuteIntentSql(options, corruption == "missing_db"
                ? "DELETE FROM input_output_selections;"
                : "UPDATE input_output_selections SET mode='UnifiedDirectory', unified_root='D:\\other';");
        else
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(original)!;
            switch (corruption)
            {
                case "old_version": node["schema_version"] = InputCommitJournalProtocol.Version; break;
                case "missing_choice": node.AsObject().Remove("output_selection"); break;
                case "unknown_choice_field": node["output_selection"]!["override"] = true; break;
                case "unknown_mode": node["output_selection"]!["mode"] = "FutureMode"; break;
                case "numeric_mode": node["output_selection"]!["mode"] = "1"; break;
                case "unexpected_root": node["output_selection"]!["unified_root"] = @"D:\guessed"; break;
                case "unknown_top_field": node["override"] = true; break;
            }
            var changed = node.ToJsonString();
            if (corruption == "duplicate_mode") changed = changed.Replace("\"mode\":", "\"mode\":\"StyleSourceDirectories\",\"mode\":");
            if (corruption == "duplicate_version") changed = changed.Replace("\"schema_version\":", "\"schema_version\":\"input-commit-journal/v1\",\"schema_version\":");
            await File.WriteAllTextAsync(journalPath, changed);
        }
        var evidence = await File.ReadAllBytesAsync(journalPath);
        var error = await Record.ExceptionAsync(() => repository.GetAsync(draft.BatchId, default));
        Assert.True(error is InvalidDataException or InvalidOperationException, error?.ToString() ?? "No rejection");
        Assert.Equal(evidence, await File.ReadAllBytesAsync(journalPath));
        Assert.Equal(BatchScanStatus.ScanReady, (await store.GetAsync(draft.BatchId, default))!.ScanStatus);
        Assert.False(Directory.Exists(command.OutputRoot));
        Assert.False(Directory.Exists(Path.Combine(command.InputRoot, "生成详情页")));
    }

    [Fact]
    public async Task OutputIntentCanChangeOnlyBeforeScanAndRepositoryAlsoEnforcesFreeze()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(Path.Combine(directory.Path, "app")));
        var (service, _, command) = await OutputIntentScenario(directory.Path, store);
        var source = await service.CreateDraftFromTemplateAsync(command);
        var unifiedCommand = command with { OutputSelection = BatchOutputSelection.Create(BatchOutputMode.UnifiedDirectory, @"D:\公开成品") };
        var unified = await service.CreateDraftFromTemplateAsync(unifiedCommand);
        Assert.Equal(source.BatchId, unified.BatchId);
        Assert.Equal(source.CreatedAt, unified.CreatedAt);
        Assert.Equal(unifiedCommand.OutputSelection, (await store.GetAsync(source.BatchId, default))!.OutputSelection);
        await store.SaveAsync(unified.BeginScanning(DateTimeOffset.UtcNow), default);
        var error = await Assert.ThrowsAsync<InputRuleViolationException>(() => store.SaveAsync(source, default));
        Assert.Equal("INPUT_OUTPUT_SELECTION_FROZEN", error.Code);
        var ready = await service.ScanAsync(source.BatchId);
        Assert.Equal(unified.OutputSelection, ready.OutputSelection);
        Assert.Equal("INPUT_DRAFT_CONFIGURATION_FROZEN", (await Assert.ThrowsAsync<InputRuleViolationException>(
            () => service.CreateDraftFromTemplateAsync(command))).Code);
        var same = await service.CreateDraftFromTemplateAsync(unifiedCommand);
        Assert.Equal(ready.ManifestDigest, same.ManifestDigest);
    }

    [Fact]
    public async Task OutputIntentWriteFailureRollsBackBaseRowAndSnapshotAndCanRetry()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(Path.Combine(directory.Path, "app"));
        await using var store = new SqliteDesktopStore(options);
        var (service, _, command) = await OutputIntentScenario(directory.Path, store);
        await ExecuteIntentSql(options, "CREATE TRIGGER fail_choice BEFORE INSERT ON input_output_selections BEGIN SELECT RAISE(ABORT,'TEST_CHOICE_FAILURE'); END;");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateDraftFromTemplateAsync(command));
        Assert.Null(await store.GetAsync(command.BatchId, default));
        await ExecuteIntentSql(options, "DROP TRIGGER fail_choice;");
        Assert.Equal(command.OutputSelection, (await service.CreateDraftFromTemplateAsync(command)).OutputSelection);
        var ready = await service.ScanAsync(command.BatchId);
        Assert.Equal(BatchScanStatus.ScanReady, ready.ScanStatus);
    }

    [Fact]
    public async Task NewOutputIntentCannotEnterLegacyRendererOrCreateOutputDirectory()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(Path.Combine(directory.Path, "app")));
        var (service, _, command) = await OutputIntentScenario(directory.Path, store);
        await service.CreateDraftFromTemplateAsync(command);
        await service.ScanAsync(command.BatchId);
        // Deliberately unavailable dependencies: admission must reject before using any renderer/secret owner.
        var factory = new Desktop.Infrastructure.Bridge.BridgeRenderBatchSessionFactory(store, null!, null!, null!, null!, store);
        var journal = new Desktop.Application.Rendering.RenderBatchJournal(1, command.BatchId, 1,
            Desktop.Application.Rendering.RenderBatchStatus.Prepared, false, false, []);
        Assert.Equal("RENDER_OUTPUT_PUBLICATION_NOT_AVAILABLE", (await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.OpenAsync(journal, default))).Message);
        Assert.False(Directory.Exists(command.OutputRoot));
    }

    [Fact]
    public async Task OutputIntentRequiresRecoveryJournalBeforeAnyScanCommitFiles()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(Path.Combine(directory.Path, "app")));
        var (service, _, command) = await OutputIntentScenario(directory.Path, store);
        var draft = await service.CreateDraftFromTemplateAsync(command);
        var scanning = draft.BeginScanning(DateTimeOffset.UtcNow);
        var committer = new SqliteBatchScanCommitter(store, CreateManifestStore(Path.Combine(directory.Path, "app")), TimeProvider.System);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => committer.CommitAsync(
            new(scanning, BatchInputType.SingleStyle, [], [], null!), default));
        Assert.Equal("INPUT_OUTPUT_SELECTION_JOURNAL_REQUIRED", error.Message);
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "app", "batches", draft.BatchId)));
    }

    private static async Task<(BatchInputService Service, RecoveringBatchRepository Repository, CreateBatchFromTemplateCommand Command)>
        OutputIntentScenario(string root, SqliteDesktopStore store)
    {
        var appRoot = Path.Combine(root, "app");
        var source = Directory.CreateDirectory(Path.Combine(root, "真实款", "新建文件夹")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(source, "front.png"), CreatePng(4, 3));
        await store.InitializeAsync();
        var (group, revision) = await SeedCommittedTemplateAsync(store);
        var manifests = CreateManifestStore(appRoot);
        var journals = CreateInputJournalStore(appRoot);
        var repository = new RecoveringBatchRepository(store, manifests, TimeProvider.System, journals);
        var service = new BatchInputService(new ReadOnlyFileInventory(), new StreamingAssetMetadataReader(), repository,
            new SqliteBatchScanCommitter(store, manifests, TimeProvider.System, journals), TimeProvider.System, templates: store);
        return (service, repository, new("batch_output_choice", group.GroupId, source, Path.Combine(root, "internal-evidence"),
            [revision.Pages[0].PageId], BatchSelectionMode.Automatic, false,
            BatchOutputSelection.Create(BatchOutputMode.StyleSourceDirectories)));
    }

    private static async Task ExecuteIntentSql(DesktopDataOptions options, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = options.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputIntentMigrationPreservesLegacyRowsAndRollsBackOnFailure(bool fail)
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(Path.Combine(directory.Path, "app"));
        string before;
        await using (var store = new SqliteDesktopStore(options))
        {
            var (service, _, command) = await OutputIntentScenario(directory.Path, store);
            await service.CreateDraftFromTemplateAsync(command with { OutputSelection = null });
            await service.ScanAsync(command.BatchId);
            before = System.Text.Json.JsonSerializer.Serialize(await store.GetAsync(command.BatchId, default));
        }
        await ExecuteIntentSql(options, LegacyMigrationFixture.RemoveTpl02Schema + "DROP TABLE render_output_publication_files; DROP TABLE render_output_publications; DROP TABLE render_output_stages; DROP TABLE render_output_registrations; DROP TABLE input_output_selections; DELETE FROM schema_migrations WHERE version>=19;"
            + (fail ? "CREATE TRIGGER fail_v19 BEFORE INSERT ON schema_migrations WHEN NEW.version=19 BEGIN SELECT RAISE(ABORT,'TEST_V19_FAILURE'); END;" : ""));
        var migrations = await ReadIntentScalar(options,
            "SELECT group_concat(version || ':' || checksum || ':' || applied_at, '|') FROM (SELECT * FROM schema_migrations ORDER BY version);");
        await using (var reopened = new SqliteDesktopStore(options))
        {
            if (fail) await Assert.ThrowsAsync<SqliteException>(() => reopened.InitializeAsync());
            else
            {
                await reopened.InitializeAsync();
                Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(await reopened.GetAsync("batch_output_choice", default)));
                Assert.Equal("0", await ReadIntentScalar(options, "SELECT count(*) FROM input_output_selections;"));
            }
        }
        Assert.Equal(migrations, await ReadIntentScalar(options,
            "SELECT group_concat(version || ':' || checksum || ':' || applied_at, '|') FROM (SELECT * FROM schema_migrations WHERE version<=18 ORDER BY version);"));
        Assert.Equal(fail ? "18" : "28", await ReadIntentScalar(options, "SELECT max(version) FROM schema_migrations;"));
        if (fail) Assert.Equal("0", await ReadIntentScalar(options,
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='input_output_selections';"));
    }

    [Fact]
    public async Task SourceModeRetainsExactMultiStyleSourceMappingWithoutCreatingPublicFolders()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(Path.Combine(directory.Path, "app"));
        string before;
        await using (var store = new SqliteDesktopStore(options))
        {
            var (service, _, command) = await OutputIntentScenario(directory.Path, store);
            var input = Directory.CreateDirectory(Path.Combine(directory.Path, "多款")).FullName;
            foreach (var name in new[] { "款A", "款B" })
            {
                var style = Directory.CreateDirectory(Path.Combine(input, name)).FullName;
                await File.WriteAllBytesAsync(Path.Combine(style, "front.png"), CreatePng(4, 3));
            }
            await service.CreateDraftFromTemplateAsync(command with { InputRoot = input });
            var ready = await service.ScanAsync(command.BatchId);
            Assert.Equal(BatchScanStatus.ScanReady, ready.ScanStatus);
            Assert.Equal(["款A", "款B"], ready.Styles.Select(s => s.SourceDirectoryRelativePath));
            Assert.Equal(2, ready.Styles.Select(s => s.StyleId).Distinct().Count());
            Assert.All(ready.Styles, s => Assert.False(Directory.Exists(Path.Combine(input, s.SourceDirectoryRelativePath, "生成详情页"))));
            before = System.Text.Json.JsonSerializer.Serialize(ready);
        }
        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        var repository = new RecoveringBatchRepository(reopened, CreateManifestStore(options.RootDirectory), TimeProvider.System,
            CreateInputJournalStore(options.RootDirectory));
        var restored = await repository.GetAsync("batch_output_choice", default);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(restored));
    }

    private static async Task<string?> ReadIntentScalar(DesktopDataOptions options, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = options.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(BatchOutputMode.UnifiedDirectory)]
    [InlineData(BatchOutputMode.StyleSourceDirectories)]
    public async Task CommittedManifestAndJournalRebuildMissingSqliteBatchGraph(BatchOutputMode? mode)
    {
        using var directory = new TestDirectory();
        var sourceRoot = Path.Combine(directory.Path, "source");
        var outputRoot = Path.Combine(directory.Path, "output");
        var appRoot = Path.Combine(directory.Path, "app");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(outputRoot);
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "front.png"), CreatePng(4, 3));
        var options = new DesktopDataOptions(appRoot);
        var selection = mode is null ? null : BatchOutputSelection.Create(mode.Value,
            mode == BatchOutputMode.UnifiedDirectory ? Path.Combine(directory.Path, "公开成品") : null);
        byte[]? journalBefore = null;

        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            var (group, revision) = await SeedCommittedTemplateAsync(store);
            var manifests = CreateManifestStore(appRoot);
            var journals = CreateInputJournalStore(appRoot);
            var repository = new RecoveringBatchRepository(
                store,
                manifests,
                TimeProvider.System,
                journals);
            var service = new BatchInputService(
                new ReadOnlyFileInventory(),
                new StreamingAssetMetadataReader(),
                repository,
                new SqliteBatchScanCommitter(
                    store,
                    manifests,
                    TimeProvider.System,
                    journals),
                TimeProvider.System,
                templates: store);
            var draft = await service.CreateDraftFromTemplateAsync(new CreateBatchFromTemplateCommand(
                "batch_journal_rebuild",
                group.GroupId,
                sourceRoot,
                outputRoot,
                [revision.Pages[0].PageId],
                BatchSelectionMode.Automatic,
                false, selection));

            var ready = await service.ScanAsync(draft.BatchId);
            Assert.Equal(BatchScanStatus.ScanReady, ready.ScanStatus);
            var journal = await journals.GetAsync(ready.BatchId, CancellationToken.None);
            Assert.NotNull(journal);
            Assert.Equal(InputCommitJournalState.Committed, journal!.State);
            Assert.Equal(selection, ready.OutputSelection);
            Assert.Equal(selection, journal.ReadyDraft.OutputSelection);
            Assert.Equal(mode is null ? InputCommitJournalProtocol.Version
                : InputCommitJournalProtocol.OutputSelectionVersion, journal.SchemaVersion);
            journalBefore = await File.ReadAllBytesAsync(Path.Combine(appRoot,
                "batches", draft.BatchId, "asset-manifest", "commit.journal.json"));
            if (mode is null) Assert.DoesNotContain("output_selection", System.Text.Encoding.UTF8.GetString(journalBefore));
        }

        await using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = options.DatabasePath,
                Pooling = false
            }.ToString()))
        {
            await connection.OpenAsync();
            await using var delete = connection.CreateCommand();
            delete.CommandText = "PRAGMA foreign_keys=ON; DELETE FROM input_batches WHERE batch_id = $batch_id;";
            delete.Parameters.AddWithValue("$batch_id", "batch_journal_rebuild");
            await delete.ExecuteNonQueryAsync();
        }

        await using var reopenedStore = new SqliteDesktopStore(options);
        await reopenedStore.InitializeAsync();
        var reopenedManifests = CreateManifestStore(appRoot);
        var reopenedJournals = CreateInputJournalStore(appRoot);
        var reopenedRepository = new RecoveringBatchRepository(
            reopenedStore,
            reopenedManifests,
            TimeProvider.System,
            reopenedJournals);

        var recovered = await reopenedRepository.GetAsync(
            "batch_journal_rebuild",
            CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.Equal(BatchScanStatus.ScanReady, recovered!.ScanStatus);
        Assert.Single(recovered.Assets);
        Assert.Equal(selection, recovered.OutputSelection);
        Assert.Equal(selection, (await reopenedStore.GetAsync(recovered.BatchId, default))!.OutputSelection);
        Assert.Equal(journalBefore, await File.ReadAllBytesAsync(Path.Combine(appRoot,
            "batches", recovered.BatchId, "asset-manifest", "commit.journal.json")));
        Assert.Equal(
            recovered.ManifestDigest,
            (await reopenedManifests.InspectAsync(
                recovered.BatchId,
                CancellationToken.None))!.ManifestDigest);
        Assert.Equal(
            recovered.BatchId,
            (await reopenedStore.GetAsync(recovered.BatchId, CancellationToken.None))!.BatchId);
    }

    [Fact]
    public async Task CommittedBatchIgnoresAssetOrderChangedBySqliteCollation()
    {
        using var directory = new TestDirectory();
        var sourceRoot = Path.Combine(directory.Path, "source");
        var outputRoot = Path.Combine(directory.Path, "output");
        var appRoot = Path.Combine(directory.Path, "app");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(outputRoot);
        // OrdinalIgnoreCase scanning puts a.png before B.png, while SQLite's
        // default binary collation puts B.png before a.png on reload.
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "a.png"), CreatePng(4, 3));
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "B.png"), CreatePng(4, 3));
        var options = new DesktopDataOptions(appRoot);

        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        var (group, revision) = await SeedCommittedTemplateAsync(store);
        var manifests = CreateManifestStore(appRoot);
        var journals = CreateInputJournalStore(appRoot);
        var repository = new RecoveringBatchRepository(
            store,
            manifests,
            TimeProvider.System,
            journals);
        var service = new BatchInputService(
            new ReadOnlyFileInventory(),
            new StreamingAssetMetadataReader(),
            repository,
            new SqliteBatchScanCommitter(
                store,
                manifests,
                TimeProvider.System,
                journals),
            TimeProvider.System,
            templates: store);
        var draft = await service.CreateDraftFromTemplateAsync(new CreateBatchFromTemplateCommand(
            "batch_asset_order_collation",
            group.GroupId,
            sourceRoot,
            outputRoot,
            [revision.Pages[0].PageId],
            BatchSelectionMode.Automatic,
            false));

        var ready = await service.ScanAsync(draft.BatchId);

        Assert.Equal(BatchScanStatus.ScanReady, ready.ScanStatus);
        Assert.Equal(2, ready.Assets.Length);
        var journal = await journals.GetAsync(ready.BatchId, CancellationToken.None);
        Assert.NotNull(journal);
        Assert.Equal(InputCommitJournalState.Committed, journal!.State);
        var manifest = await manifests.InspectAsync(ready.BatchId, CancellationToken.None);
        Assert.NotNull(manifest);
        Assert.True(manifest!.IsCommitted);
        Assert.Equal(ready.ManifestDigest, manifest.ManifestDigest);
    }

    [Fact]
    public async Task CommittedManifestWithoutDatabaseRowIsExplicitMismatch()
    {
        using var directory = new TestDirectory();
        var appRoot = Path.Combine(directory.Path, "app");
        var manifests = CreateManifestStore(appRoot);
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(appRoot));
        await store.InitializeAsync();
        var revision = CommittedRevision("revision_orphan", "group_orphan", includeSecondPage: false);
        var snapshot = BatchTemplateSnapshot.Create(
            "snapshot_orphan",
            "batch_orphan_manifest",
            revision,
            ["page_input_01"],
            DateTimeOffset.UtcNow);
        var scanning = BatchDraft.Create(
            "batch_orphan_manifest",
            directory.Path,
            directory.Path,
            snapshot,
            BatchSelectionMode.Automatic,
            false,
            DateTimeOffset.UtcNow).BeginScanning(DateTimeOffset.UtcNow);
        var style = StyleDraft.Create("style_orphan", "款式", "", 0, ["asset_orphan"]);
        var asset = SourceAssetMetadata.CreateReadable(
            "asset_orphan",
            style.StyleId,
            "image.png",
            "image.png",
            100,
            new string('a', 64),
            1,
            1,
            SourceImageFormat.Png,
            "image/png");
        var manifest = AssetManifestV1.Create(
            scanning,
            BatchInputType.SingleStyle,
            [style],
            [asset],
            new string('b', 64),
            DateTimeOffset.UtcNow);
        var staged = await manifests.StageAsync(manifest, CancellationToken.None);
        await manifests.CommitAsync(staged, CancellationToken.None);
        var repository = new RecoveringBatchRepository(store, manifests, TimeProvider.System);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.GetAsync(scanning.BatchId, CancellationToken.None));

        Assert.Equal("INPUT_MANIFEST_DATABASE_MISMATCH", error.Message);
    }

    [Fact]
    public async Task ManifestCommitFailureKeepsJournalStageForRestartRecovery()
    {
        using var directory = new TestDirectory();
        var sourceRoot = Path.Combine(directory.Path, "source");
        var outputRoot = Path.Combine(directory.Path, "output");
        var appRoot = Path.Combine(directory.Path, "app");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(outputRoot);
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "front.png"), CreatePng(4, 3));
        var options = new DesktopDataOptions(appRoot);

        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            var (group, revision) = await SeedCommittedTemplateAsync(store);
            var manifests = CreateManifestStore(appRoot);
            var failingManifests = new FailOnceManifestStore(manifests);
            var journals = CreateInputJournalStore(appRoot);
            var repository = new RecoveringBatchRepository(
                store,
                manifests,
                TimeProvider.System,
                journals);
            var service = new BatchInputService(
                new ReadOnlyFileInventory(),
                new StreamingAssetMetadataReader(),
                repository,
                new SqliteBatchScanCommitter(
                    store,
                    failingManifests,
                    TimeProvider.System,
                    journals),
                TimeProvider.System,
                templates: store);
            var draft = await service.CreateDraftFromTemplateAsync(new CreateBatchFromTemplateCommand(
                "batch_manifest_recovery",
                group.GroupId,
                sourceRoot,
                outputRoot,
                [revision.Pages[0].PageId],
                BatchSelectionMode.Automatic,
                false));

            var blocked = await service.ScanAsync(draft.BatchId);

            Assert.Equal(BatchScanStatus.ScanBlocked, blocked.ScanStatus);
            Assert.Equal("INPUT_PERSISTENCE_FAILURE", blocked.BlockReason?.Code);
            var journal = await journals.GetAsync(draft.BatchId, CancellationToken.None);
            Assert.NotNull(journal);
            Assert.Equal(InputCommitJournalState.RecoveryRequired, journal!.State);
            var staged = await manifests.InspectAsync(draft.BatchId, CancellationToken.None);
            Assert.NotNull(staged);
            Assert.True(staged!.IsStaged);
            Assert.False(staged.IsCommitted);
            Assert.Equal(BatchScanStatus.ScanReady, (await store.GetAsync(draft.BatchId, CancellationToken.None))!.ScanStatus);
        }

        await using var reopenedStore = new SqliteDesktopStore(options);
        await reopenedStore.InitializeAsync();
        var reopenedManifests = CreateManifestStore(appRoot);
        var reopenedJournals = CreateInputJournalStore(appRoot);
        var reopenedRepository = new RecoveringBatchRepository(
            reopenedStore,
            reopenedManifests,
            TimeProvider.System,
            reopenedJournals);

        var recovered = await reopenedRepository.GetAsync(
            "batch_manifest_recovery",
            CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.Equal(BatchScanStatus.ScanReady, recovered!.ScanStatus);
        var committed = await reopenedManifests.InspectAsync(
            recovered.BatchId,
            CancellationToken.None);
        Assert.NotNull(committed);
        Assert.True(committed!.IsCommitted);
        Assert.False(committed.IsStaged);
        Assert.Equal(
            InputCommitJournalState.Committed,
            (await reopenedJournals.GetAsync(recovered.BatchId, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task TwoHundredStyleBatchCommitsManifestPreservesSourcesAndSurvivesRestart()
    {
        using var directory = new TestDirectory();
        var sourceRoot = Path.Combine(directory.Path, "客户 输入 Source 2026");
        var outputRoot = Path.Combine(directory.Path, "正式 输出");
        var appRoot = Path.Combine(directory.Path, "app-owned");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(outputRoot);
        var sourceFiles = new List<string>();
        for (var index = 0; index < 200; index++)
        {
            var styleDirectory = Path.Combine(sourceRoot, $"款式 Style {index:D3}");
            Directory.CreateDirectory(styleDirectory);
            var file = Path.Combine(styleDirectory, $"商品 图 {index:D3}.png");
            await File.WriteAllBytesAsync(file, CreatePng(width: 3, height: 2));
            sourceFiles.Add(file);
        }
        var hashesBefore = await HashFilesAsync(sourceFiles);

        var options = new DesktopDataOptions(appRoot);
        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            var (group, revision) = await SeedCommittedTemplateAsync(store);
            var manifestStore = CreateManifestStore(appRoot);
            var repository = new RecoveringBatchRepository(store, manifestStore, TimeProvider.System);
            var service = new BatchInputService(
                new ReadOnlyFileInventory(),
                new StreamingAssetMetadataReader(),
                repository,
                new SqliteBatchScanCommitter(store, manifestStore, TimeProvider.System),
                TimeProvider.System,
                new BatchScanOptions(4),
                store);
            var draft = await service.CreateDraftFromTemplateAsync(new CreateBatchFromTemplateCommand(
                "batch_200_styles",
                group.GroupId,
                sourceRoot,
                outputRoot,
                [revision.Pages[0].PageId],
                BatchSelectionMode.Automatic,
                false));

            var ready = await service.ScanAsync(draft.BatchId);

            Assert.Equal(BatchScanStatus.ScanReady, ready.ScanStatus);
            Assert.Equal(BatchInputType.StyleDirectoryBatch, ready.InputType);
            Assert.Equal(200, ready.Styles.Length);
            Assert.Equal(200, ready.Assets.Length);
            Assert.All(ready.Assets, asset =>
            {
                Assert.False(Path.IsPathRooted(asset.SourceRelativePath));
                Assert.Equal(AssetReadability.Readable, asset.Readability);
                Assert.Equal(SourceImageFormat.Png, asset.Format);
            });
            Assert.NotNull(await manifestStore.InspectAsync(ready.BatchId, CancellationToken.None));
            Assert.Equal(hashesBefore, await HashFilesAsync(sourceFiles));

            var removable = await service.CreateDraftFromTemplateAsync(new CreateBatchFromTemplateCommand(
                "batch_delete_source_safe",
                group.GroupId,
                sourceRoot,
                outputRoot,
                [revision.Pages[0].PageId],
                BatchSelectionMode.Automatic,
                false));
            await repository.DeleteDraftAsync(removable.BatchId, CancellationToken.None);
            Assert.Null(await repository.GetAsync(removable.BatchId, CancellationToken.None));
            Assert.Equal(hashesBefore, await HashFilesAsync(sourceFiles));
        }

        await using var reopenedStore = new SqliteDesktopStore(options);
        await reopenedStore.InitializeAsync();
        var reopenedManifestStore = CreateManifestStore(appRoot);
        var reopenedRepository = new RecoveringBatchRepository(
            reopenedStore,
            reopenedManifestStore,
            TimeProvider.System);
        var recovered = await reopenedRepository.GetAsync("batch_200_styles", CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.Equal(BatchScanStatus.ScanReady, recovered.ScanStatus);
        Assert.Equal(200, recovered.Styles.Length);
        Assert.Equal(200, recovered.Assets.Length);
        Assert.NotNull(await reopenedStore.GetGroupAsync("group_input", CancellationToken.None));
        Assert.Equal(hashesBefore, await HashFilesAsync(sourceFiles));
    }

    [Fact]
    public async Task ReaderAcceptsProgressiveJpegWithoutChangingSourceBytes()
    {
        using var directory = new TestDirectory();
        var bytes = CreateProgressiveJpeg();
        var path = Path.Combine(directory.Path, "渐进式.jpg");
        await File.WriteAllBytesAsync(path, bytes);

        var result = await new StreamingAssetMetadataReader().ReadAsync(
            directory.Path, Path.GetFileName(path), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Failure?.Code);
        Assert.Equal((16, 12, SourceImageFormat.Jpeg),
            (result.Metadata!.PixelWidth, result.Metadata.PixelHeight, result.Metadata.Format));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), result.Metadata.Sha256);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("missing-eoi")]
    [InlineData("truncated-marker")]
    [InlineData("short-later-segment")]
    [InlineData("oversized-later-segment")]
    [InlineData("short-scan-header")]
    [InlineData("no-scan")]
    [InlineData("extension-mismatch")]
    public async Task ReaderStillRejectsDamagedProgressiveJpeg(string damage)
    {
        using var directory = new TestDirectory();
        var bytes = CreateProgressiveJpeg();
        var scan = bytes.AsSpan().IndexOf(new byte[] { 0xff, 0xda });
        var nextTable = scan + 2 + bytes.AsSpan(scan + 2).IndexOf(new byte[] { 0xff, 0xc4 });
        Assert.True(nextTable > scan);
        switch (damage)
        {
            case "missing-eoi": bytes = bytes[..^2]; break;
            case "truncated-marker": bytes = bytes[..(nextTable + 1)]; break;
            case "short-later-segment": bytes[nextTable + 2] = 0; bytes[nextTable + 3] = 1; break;
            case "oversized-later-segment": bytes[nextTable + 2] = 0xff; bytes[nextTable + 3] = 0xff; break;
            case "short-scan-header": bytes[scan + 2] = 0; bytes[scan + 3] = 1; break;
            case "no-scan": bytes = [.. CreateJpeg(3, 2)[..15], 0xff, 0xd9]; break;
        }
        var path = Path.Combine(directory.Path, damage == "extension-mismatch" ? "image.png" : "image.jpg");
        await File.WriteAllBytesAsync(path, bytes);
        var result = await new StreamingAssetMetadataReader().ReadAsync(
            directory.Path, Path.GetFileName(path), CancellationToken.None);
        Assert.Equal("INPUT_ASSET_CORRUPT", result.Failure!.Code);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ReaderKeepsJpegStuffedBytesRestartAndFillInsideEntropyScan()
    {
        using var directory = new TestDirectory();
        // Structural marker fixture, not a claim of a fully decoded image.
        byte[] bytes = [.. CreateJpeg(3, 2)[..^2], 0xff, 0x00, 0x12, 0xff, 0xd0, 0x34, 0xff, 0xff, 0xd9];
        var path = Path.Combine(directory.Path, "markers.jpg");
        await File.WriteAllBytesAsync(path, bytes);
        var result = await new StreamingAssetMetadataReader().ReadAsync(
            directory.Path, Path.GetFileName(path), CancellationToken.None);
        Assert.True(result.IsSuccess, result.Failure?.Code);
        Assert.Equal((3, 2), (result.Metadata!.PixelWidth, result.Metadata.PixelHeight));
    }

    [Fact]
    public async Task ReadersAcceptSupportedFormatsAndBlockCorruptMismatchDeepAndEscapingPaths()
    {
        using var directory = new TestDirectory();
        var root = Path.Combine(directory.Path, "中文 English 123 空格");
        Directory.CreateDirectory(root);
        var jpegPath = Path.Combine(root, "正面 01.jpg");
        var pngPath = Path.Combine(root, "detail_02.png");
        var tiffPath = Path.Combine(root, "材质 03.tiff");
        await File.WriteAllBytesAsync(jpegPath, CreateJpeg(3, 2));
        await File.WriteAllBytesAsync(pngPath, CreatePng(4, 5));
        await File.WriteAllBytesAsync(tiffPath, CreateTiff(6, 7));
        var longStyleDirectory = Path.Combine(root, "长路径款式 " + new string('A', 140));
        Directory.CreateDirectory(longStyleDirectory);
        var longFilePath = Path.Combine(longStyleDirectory, "Long 中文 123 " + new string('B', 100) + ".png");
        await File.WriteAllBytesAsync(longFilePath, CreatePng(8, 9));
        Assert.True(longFilePath.Length > 260);
        var hashesBefore = await HashFilesAsync([jpegPath, pngPath, tiffPath, longFilePath]);
        var reader = new StreamingAssetMetadataReader();

        var jpeg = await reader.ReadAsync(root, Path.GetFileName(jpegPath), CancellationToken.None);
        var png = await reader.ReadAsync(root, Path.GetFileName(pngPath), CancellationToken.None);
        var tiff = await reader.ReadAsync(root, Path.GetFileName(tiffPath), CancellationToken.None);
        var longPath = await reader.ReadAsync(
            root,
            Path.GetRelativePath(root, longFilePath),
            CancellationToken.None);

        Assert.Equal((3, 2, SourceImageFormat.Jpeg),
            (jpeg.Metadata!.PixelWidth, jpeg.Metadata.PixelHeight, jpeg.Metadata.Format));
        Assert.Equal((4, 5, SourceImageFormat.Png),
            (png.Metadata!.PixelWidth, png.Metadata.PixelHeight, png.Metadata.Format));
        Assert.Equal((6, 7, SourceImageFormat.Tiff),
            (tiff.Metadata!.PixelWidth, tiff.Metadata.PixelHeight, tiff.Metadata.Format));
        Assert.Equal((8, 9), (longPath.Metadata!.PixelWidth, longPath.Metadata.PixelHeight));
        Assert.Equal(hashesBefore, await HashFilesAsync([jpegPath, pngPath, tiffPath, longFilePath]));

        var mismatchPath = Path.Combine(root, "扩展名不符.png");
        var corruptPath = Path.Combine(root, "损坏.jpg");
        var truncatedTiffPath = Path.Combine(root, "截断.tif");
        await File.WriteAllBytesAsync(mismatchPath, CreateJpeg(1, 1));
        await File.WriteAllBytesAsync(corruptPath, [0xff, 0xd8, 0xff]);
        var truncated = CreateTiff(2, 2)[..^1];
        await File.WriteAllBytesAsync(truncatedTiffPath, truncated);
        var deepDirectory = Path.Combine(root, "一级", "二级");
        Directory.CreateDirectory(deepDirectory);
        await File.WriteAllBytesAsync(Path.Combine(deepDirectory, "deep.png"), CreatePng(1, 1));

        Assert.Equal("INPUT_ASSET_CORRUPT",
            (await reader.ReadAsync(root, Path.GetFileName(mismatchPath), CancellationToken.None)).Failure!.Code);
        Assert.Equal("INPUT_ASSET_CORRUPT",
            (await reader.ReadAsync(root, Path.GetFileName(corruptPath), CancellationToken.None)).Failure!.Code);
        Assert.Equal("INPUT_ASSET_CORRUPT",
            (await reader.ReadAsync(root, Path.GetFileName(truncatedTiffPath), CancellationToken.None)).Failure!.Code);
        Assert.Equal("INPUT_ASSET_UNREADABLE",
            (await reader.ReadAsync(root, "一级/二级/deep.png", CancellationToken.None)).Failure!.Code);
        Assert.Equal("INPUT_ASSET_UNREADABLE",
            (await reader.ReadAsync(root, "../outside.png", CancellationToken.None)).Failure!.Code);

        var inventory = await new ReadOnlyFileInventory().ReadApprovedLevelsAsync(root, CancellationToken.None);
        Assert.True(inventory.IsSuccess);
        Assert.DoesNotContain(
            inventory.Inventory!.FirstLevelDirectories.SelectMany(item => item.DirectFiles),
            file => file.RelativePath.Contains("deep.png", StringComparison.Ordinal));

        var lockedPath = Path.Combine(root, "被占用.png");
        await File.WriteAllBytesAsync(lockedPath, CreatePng(1, 1));
        await using (var locked = new FileStream(
            lockedPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            var unreadable = await reader.ReadAsync(root, Path.GetFileName(lockedPath), CancellationToken.None);
            Assert.Equal("INPUT_ASSET_UNREADABLE", unreadable.Failure!.Code);
        }

        var disappeared = await new ReadOnlyFileInventory().ReadApprovedLevelsAsync(
            Path.Combine(root, "已经消失"),
            CancellationToken.None);
        Assert.Equal("INPUT_DIRECTORY_DISAPPEARED", disappeared.Failure!.Code);
    }

    [Fact]
    public async Task FirstLevelDirectoryReparsePointIsNeverFollowedWhenPlatformAllowsCreation()
    {
        using var directory = new TestDirectory();
        var root = Path.Combine(directory.Path, "root");
        var outside = Path.Combine(directory.Path, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        await File.WriteAllBytesAsync(Path.Combine(outside, "outside.png"), CreatePng(1, 1));
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "linked-style"), outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return;
        }

        var result = await new ReadOnlyFileInventory().ReadApprovedLevelsAsync(root, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("INPUT_DIRECTORY_ACCESS_DENIED", result.Failure!.Code);
    }

    [Fact]
    public async Task DraftSnapshotCanUpdateBeforeScanButScanningSnapshotIsFrozenInSqlite()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(Path.Combine(directory.Path, "app"));
        var now = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        var (group, revision) = await SeedCommittedTemplateAsync(store, includeSecondPage: true);
        var manifestStore = CreateManifestStore(options.RootDirectory);
        var repository = new RecoveringBatchRepository(store, manifestStore, TimeProvider.System);
        var service = new BatchInputService(
            new ReadOnlyFileInventory(),
            new StreamingAssetMetadataReader(),
            repository,
            new SqliteBatchScanCommitter(store, manifestStore, TimeProvider.System),
            new FixedTimeProvider(now),
            templates: store);
        var first = await service.CreateDraftFromTemplateAsync(new CreateBatchFromTemplateCommand(
            "batch_autosave",
            group.GroupId,
            directory.Path,
            directory.Path,
            ["page_input_01"],
            BatchSelectionMode.Automatic,
            false));

        var updated = await service.CreateDraftFromTemplateAsync(new CreateBatchFromTemplateCommand(
            first.BatchId,
            group.GroupId,
            directory.Path,
            Path.Combine(directory.Path, "new-output"),
            ["page_input_02"],
            BatchSelectionMode.Manual,
            true));
        var restored = await store.GetAsync(first.BatchId, CancellationToken.None);

        Assert.Equal(first.TemplateSnapshot.SnapshotId, updated.TemplateSnapshot.SnapshotId);
        Assert.Equal("page_input_02", Assert.Single(restored!.TemplateSnapshot.Pages).PageId);
        Assert.Equal(BatchSelectionMode.Manual, restored.SelectionMode);
        var scanning = updated.BeginScanning(now);
        await store.SaveAsync(scanning, CancellationToken.None);
        var changedSnapshot = BatchTemplateSnapshot.Create(
            scanning.TemplateSnapshot.SnapshotId,
            scanning.BatchId,
            revision,
            ["page_input_01"],
            scanning.TemplateSnapshot.CreatedAt);
        var conflicting = BatchDraft.Restore(
            scanning.BatchId,
            BatchScanStatus.Scanning,
            null,
            scanning.InputRoot,
            scanning.OutputRoot,
            changedSnapshot,
            scanning.SelectionMode,
            scanning.IncludeEditablePsd,
            [],
            [],
            null,
            null,
            null,
            scanning.CreatedAt,
            scanning.UpdatedAt);

        var error = await Assert.ThrowsAsync<InputRuleViolationException>(
            () => store.SaveAsync(conflicting, CancellationToken.None));
        Assert.Equal("INPUT_TEMPLATE_SNAPSHOT_CONFLICT", error.Code);
    }

    [Fact]
    public async Task DatabaseCommitFailureRollsBackApplicationOwnedManifestWithoutReadyState()
    {
        using var directory = new TestDirectory();
        var appRoot = Path.Combine(directory.Path, "app");
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(appRoot));
        await store.InitializeAsync();
        var revision = CommittedRevision("missing_revision", "missing_group", includeSecondPage: false);
        var snapshot = BatchTemplateSnapshot.Create(
            "snapshot_db_failure",
            "batch_db_failure",
            revision,
            ["page_input_01"],
            DateTimeOffset.UtcNow);
        var scanning = BatchDraft.Create(
            "batch_db_failure",
            directory.Path,
            directory.Path,
            snapshot,
            BatchSelectionMode.Automatic,
            false,
            DateTimeOffset.UtcNow).BeginScanning(DateTimeOffset.UtcNow);
        var style = StyleDraft.Create("style_db_failure", "款式", "", 0, ["asset_db_failure"]);
        var asset = SourceAssetMetadata.CreateReadable(
            "asset_db_failure",
            style.StyleId,
            "image.png",
            "image.png",
            100,
            new string('a', 64),
            1,
            1,
            SourceImageFormat.Png,
            "image/png");
        var manifest = AssetManifestV1.Create(
            scanning,
            BatchInputType.SingleStyle,
            [style],
            [asset],
            new string('b', 64),
            DateTimeOffset.UtcNow);
        var manifests = CreateManifestStore(appRoot);
        var committer = new SqliteBatchScanCommitter(store, manifests, TimeProvider.System);

        var result = await committer.CommitAsync(
            new BatchScanCommitRequest(scanning, BatchInputType.SingleStyle, [style], [asset], manifest),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("INPUT_SCAN_SYSTEM_FAILURE", result.Failure!.Code);
        Assert.Null(await store.GetAsync(scanning.BatchId, CancellationToken.None));
        Assert.Null(await manifests.InspectAsync(scanning.BatchId, CancellationToken.None));
    }

    private static AssetManifestFileStore CreateManifestStore(string appRoot) => new(
        new AtomicFileStore(new LocalAppPathPolicy(appRoot)),
        new JsonSchemaContractValidator());

    private static JsonInputCommitJournalStore CreateInputJournalStore(string appRoot) => new(
        new AtomicFileStore(new LocalAppPathPolicy(appRoot)),
        new LocalAppPathPolicy(appRoot));

    private sealed class FailOnceManifestStore(IAssetManifestStore inner) : IAssetManifestStore
    {
        private int _failed;

        public Task<StagedAssetManifest> StageAsync(
            AssetManifestV1 manifest,
            CancellationToken cancellationToken) => inner.StageAsync(manifest, cancellationToken);

        public Task CommitAsync(
            StagedAssetManifest stagedManifest,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _failed, 1) == 0)
            {
                throw new IOException("TEST_MANIFEST_COMMIT_FAILURE");
            }

            return inner.CommitAsync(stagedManifest, cancellationToken);
        }

        public Task RollbackAsync(
            StagedAssetManifest stagedManifest,
            CancellationToken cancellationToken) => inner.RollbackAsync(stagedManifest, cancellationToken);

        public Task<AssetManifestInspection?> InspectAsync(
            string batchId,
            CancellationToken cancellationToken) => inner.InspectAsync(batchId, cancellationToken);
    }

    private static async Task<(TemplateGroup Group, TemplateRevision Revision)> SeedCommittedTemplateAsync(
        SqliteDesktopStore store,
        bool includeSecondPage = false)
    {
        var group = TemplateGroup.Create("group_input", "INPUT 测试模板", DateTimeOffset.UtcNow);
        var revision = CommittedRevision("revision_input", group.GroupId, includeSecondPage);
        await store.SaveGroupAsync(group, CancellationToken.None);
        await store.CommitRevisionAsync(revision, null, CancellationToken.None);
        return ((await store.GetGroupAsync(group.GroupId, CancellationToken.None))!, revision);
    }

    private static TemplateRevision CommittedRevision(
        string revisionId,
        string groupId,
        bool includeSecondPage)
    {
        var pages = new List<TemplatePage>
        {
            Page("page_input_01", revisionId, 0)
        };
        if (includeSecondPage)
        {
            pages.Add(Page("page_input_02", revisionId, 1));
        }

        var revision = TemplateRevision.CreateDraft(
            revisionId,
            groupId,
            1,
            DateTimeOffset.UtcNow,
            pages);
        var importing = revision.AdvanceTo(
            TemplateRevisionState.ImportingCopy,
            new RevisionTransitionEvidence(SourceReadable: true));
        var inspecting = importing.AdvanceTo(
            TemplateRevisionState.StructureInspecting,
            new RevisionTransitionEvidence(CopyMatchesSource: true));
        var binding = inspecting.AdvanceTo(
            TemplateRevisionState.FrameBinding,
            new RevisionTransitionEvidence(StructureReadable: true));
        var calibrating = binding.AdvanceTo(
            TemplateRevisionState.Calibrating,
            new RevisionTransitionEvidence(RegisteredFramesHaveEvidence: true));
        var review = calibrating.AdvanceTo(
            TemplateRevisionState.AwaitingReview,
            new RevisionTransitionEvidence(CalibrationComplete: true));
        var committing = review.AdvanceTo(
            TemplateRevisionState.Committing,
            new RevisionTransitionEvidence(ConfigurationValid: true));
        return committing.Commit(
            $"templates/groups/{groupId}/revisions/{revisionId}/revision-manifest.json",
            RuleDigest,
            new RevisionTransitionEvidence(CommitArtifactsConsistent: true));
    }

    private static TemplatePage Page(string pageId, string revisionId, int order) => TemplatePage.Create(
        pageId,
        revisionId,
        order,
        pageId,
        TemplatePageContentType.NormalComposition,
        TemplateGenerationMode.OncePerProduct,
        null,
        enabled: true,
        $"resource_{pageId}");

    private static async Task<string[]> HashFilesAsync(IEnumerable<string> paths)
    {
        var results = new List<string>();
        foreach (var path in paths.Order(StringComparer.Ordinal))
        {
            results.Add(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))));
        }
        return [.. results];
    }

    // Original 16x12 RGB gradient encoded with Pillow, progressive=True; no customer media.
    private static byte[] CreateProgressiveJpeg() => Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAUDBAQEAwUEBAQFBQUGBwwIBwcHBw8LCwkMEQ8SEhEPERETFhwXExQaFRERGCEYGh0dHx8fExciJCIeJBweHx7/2wBDAQUFBQcGBw4ICA4eFBEUHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh7/wgARCAAMABADASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAABgX/xAAVAQEBAAAAAAAAAAAAAAAAAAABA//aAAwDAQACEAMQAAABGJr6Yj//xAAXEAEBAQEAAAAAAAAAAAAAAAAEAAID/9oACAEBAAEFAjhjhj8sR+WL/8QAFxEAAwEAAAAAAAAAAAAAAAAAAAMFIf/aAAgBAwEBPwGLaZh//8QAFhEBAQEAAAAAAAAAAAAAAAAAAwAB/9oACAECAQE/ARbb/8QAFRABAQAAAAAAAAAAAAAAAAAAADH/2gAIAQEABj8CiIj/xAAWEAEBAQAAAAAAAAAAAAAAAAAAITH/2gAIAQEAAT8hzc2cnP/aAAwDAQACAAMAAAAQr//EABURAQEAAAAAAAAAAAAAAAAAAABR/9oACAEDAQE/ELD/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAECAQE/EE//xAAWEAEBAQAAAAAAAAAAAAAAAADxABD/2gAIAQEAAT8QIiNzn//Z");

    private static byte[] CreateJpeg(ushort width, ushort height) =>
    [
        0xff, 0xd8,
        0xff, 0xc0, 0x00, 0x0b, 0x08,
        (byte)(height >> 8), (byte)height,
        (byte)(width >> 8), (byte)width,
        0x01, 0x01, 0x11, 0x00,
        0xff, 0xda, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3f, 0x00,
        0x01, 0xff, 0xd9
    ];

    private static byte[] CreatePng(uint width, uint height)
    {
        var result = new List<byte>(80) { 137, 80, 78, 71, 13, 10, 26, 10 };
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header[..4], width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..8], height);
        header[8] = 8;
        header[9] = 2;
        WritePngChunk(result, "IHDR"u8, header);
        WritePngChunk(result, "IDAT"u8, [0x00]);
        WritePngChunk(result, "IEND"u8, []);
        return [.. result];
    }

    private static void WritePngChunk(List<byte> output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.AddRange(length);
        output.AddRange(type);
        output.AddRange(data);
        var crcInput = new byte[type.Length + data.Length];
        type.CopyTo(crcInput);
        data.CopyTo(crcInput.AsSpan(type.Length));
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, PngCrc32(crcInput));
        output.AddRange(crc);
    }

    private static uint PngCrc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
            }
        }
        return ~crc;
    }

    private static byte[] CreateTiff(uint width, uint height)
    {
        var bytes = new byte[63];
        bytes[0] = (byte)'I';
        bytes[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2, 2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), 4);
        WriteTiffEntry(bytes.AsSpan(10, 12), 256, 4, 1, width);
        WriteTiffEntry(bytes.AsSpan(22, 12), 257, 4, 1, height);
        WriteTiffEntry(bytes.AsSpan(34, 12), 273, 4, 1, 62);
        WriteTiffEntry(bytes.AsSpan(46, 12), 279, 4, 1, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(58, 4), 0);
        bytes[62] = 0x7f;
        return bytes;
    }

    private static void WriteTiffEntry(
        Span<byte> entry,
        ushort tag,
        ushort type,
        uint count,
        uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(entry[..2], tag);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[2..4], type);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[4..8], count);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..12], value);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
