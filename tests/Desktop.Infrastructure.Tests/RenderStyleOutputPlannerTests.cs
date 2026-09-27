using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;
using Desktop.Infrastructure.Bridge;

namespace Desktop.Infrastructure.Tests;

public sealed class RenderStyleOutputPlannerTests
{
    [Theory]
    [InlineData(@"C:\Users\u\AppData\Local\app\evidence", @"C:\Users\u\AppData\Local\Packages\Example_123\LocalCache\Local\app\evidence", true)]
    [InlineData(@"C:\Users\u\AppData\Local\app\evidence", @"c:\users\u\appdata\local\packages\Example_123\localcache\local\APP\EVIDENCE", true)]
    [InlineData(@"C:\Users\u\AppData\Locality\app", @"C:\Users\u\AppData\Local\Packages\Example_123\LocalCache\Local\app", false)]
    [InlineData(@"C:\Users\u\AppData\Local\app", @"C:\Users\other\AppData\Local\Packages\Example_123\LocalCache\Local\app", false)]
    [InlineData(@"C:\Users\u\AppData\Local\app", @"D:\Users\u\AppData\Local\Packages\Example_123\LocalCache\Local\app", false)]
    [InlineData(@"C:\Users\u\AppData\Local\app", @"C:\Users\u\AppData\Local\Packages\Example_123\LocalCache\Roaming\app", false)]
    [InlineData(@"C:\Users\u\AppData\Local\app", @"C:\Users\u\AppData\Local\Packages\Example_123\LocalState\app", false)]
    [InlineData(@"C:\Users\u\AppData\Local\app", @"C:\Users\u\AppData\Local\Packages\Example_123\LocalCache\Local\other", false)]
    [InlineData(@"C:\Users\u\AppData\Local\app", @"C:\Users\u\AppData\Local\Packages\Example_123\LocalCache\Local\app\child", false)]
    [InlineData(@"C:\Users\u\AppData\Local\Packages\app", @"C:\Users\u\AppData\Local\Packages\Example_123\LocalCache\Local\Packages\app", false)]
    public void EvidenceVirtualizationMappingRequiresExactUserRootAndRelativeSuffix(string logical, string physical, bool accepted) =>
        Assert.Equal(accepted, RenderOutputDirectoryLease.IsLocalAppDataMapping(logical, physical, @"C:\Users\u\AppData\Local"));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void EvidenceMappingCannotGrantCreationOrRenameRights(bool create, bool rename)
    {
        using var fixture = new Fixture();
        Assert.Equal("RENDER_OUTPUT_DIRECTORY_INVALID", Assert.Throws<RenderOutputTargetException>(() =>
            RenderOutputDirectoryLease.Open(fixture.Input, create, rename, allowVirtualizedLocalAppData: true)).Code);
    }

    [Fact]
    public void NewlyCreatedLocalAppDataEvidencePinsItsActualAncestryWithoutRelaxingSourceLeases()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "edp-evidence-test-" + Guid.NewGuid().ToString("N"));
        var leaf = Directory.CreateDirectory(Path.Combine(root, "evidence")).FullName;
        try
        {
            using var lease = RenderOutputDirectoryLease.Open(leaf, allowVirtualizedLocalAppData: true);
            lease.Verify();
            using var physical = RenderOutputDirectoryLease.Open(lease.PhysicalPath);
            Assert.Equal(physical.Identity, lease.Identity);
            Assert.Throws<IOException>(() => Directory.Move(lease.PhysicalPath, lease.PhysicalPath + "-swapped"));
            Assert.Throws<IOException>(() => Directory.Move(Path.GetDirectoryName(lease.PhysicalPath)!, lease.PhysicalPath + "-parent"));
            if (!lease.PhysicalPath.Equals(leaf, StringComparison.OrdinalIgnoreCase))
                Assert.Throws<RenderOutputTargetException>(() => RenderOutputDirectoryLease.Open(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SourceModeUsesExactSelectedDirectoryNotParentSkuAndDoesNotWrite()
    {
        using var fixture = new Fixture();
        var batch = fixture.Batch();
        var before = fixture.Inventory();
        using (var preflight = RenderStyleOutputPlanner.Preflight(batch, default))
        {
            var target = Assert.Single(preflight.Targets);
            Assert.Equal(fixture.Input, target.SourceDirectory);
            Assert.Equal(fixture.Input, target.ParentDirectory);
            Assert.Equal(Path.Combine(fixture.Input, "生成详情页"), target.FinalDirectory);
            Assert.Equal(target.ParentDirectory, Path.GetDirectoryName(target.StagingDirectory));
            Assert.StartsWith(".edp-stage-", Path.GetFileName(target.StagingDirectory), StringComparison.Ordinal);
            Assert.Equal(preflight.GetIdentity(target.SourceDirectory), preflight.GetIdentity(target.ParentDirectory));
            preflight.RevalidateAvailable(default);
        }
        Assert.Equal(before, fixture.Inventory());
        Assert.Equal("source media sentinel", File.ReadAllText(Path.Combine(fixture.Input, "source.jpg")));
        Assert.False(Directory.Exists(fixture.Evidence));
    }

    [Fact]
    public void MultipleStylesResolveToTheirOwnFrozenSourcesIncludingSameDisplayName()
    {
        using var fixture = new Fixture();
        var batch = fixture.Batch(styles: [("同名款", "实际A"), ("同名款", "实际B")]);
        var before = fixture.Inventory();
        using var preflight = RenderStyleOutputPlanner.Preflight(batch, default);
        Assert.Equal(2, preflight.Targets.Length);
        for (var index = 0; index < 2; index++)
        {
            var target = preflight.Targets[index];
            Assert.Equal("style_" + index, target.StyleId);
            Assert.Equal(Path.Combine(fixture.Input, index == 0 ? "实际A" : "实际B", "生成详情页"), target.FinalDirectory);
            Assert.Equal(target.ParentDirectory, Path.GetDirectoryName(target.StagingDirectory));
        }
        Assert.NotEqual(preflight.GetIdentity(preflight.Targets[0].SourceDirectory), preflight.GetIdentity(preflight.Targets[1].SourceDirectory));
        Assert.Equal(before, fixture.Inventory());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnifiedModeProducesStableNamesIndependentOfEnumerationAndCulture(bool reverse)
    {
        using var fixture = new Fixture();
        var batch = fixture.Batch(unified: true, styles: [("A:款", "a"), ("A?款", "b"), ("A_款-01", "c"), ("独立款", "d")]);
        if (reverse) batch = WithStyles(batch, batch.Styles.Reverse());
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            using var preflight = RenderStyleOutputPlanner.Preflight(batch, default);
            Assert.Equal(["A_款-02", "A_款-03", "A_款-01", "独立款"], preflight.Targets.Select(item => item.DirectoryName));
            Assert.All(preflight.Targets, target => Assert.Equal(Path.Combine(fixture.Public, target.DirectoryName), target.FinalDirectory));
        }
        finally { CultureInfo.CurrentCulture = previous; }
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Public));
    }

    [Theory]
    [InlineData("CON", "款式-CON")]
    [InlineData("nul.jpg", "款式-nul.jpg")]
    [InlineData("Lpt¹.name", "款式-Lpt¹.name")]
    [InlineData("conin$", "款式-conin$")]
    [InlineData("..", "款式")]
    [InlineData(" \"/\\?:*|<>. ", "款式")]
    [InlineData("A\u202e款\n ", "A_款")]
    [InlineData("e\u0301款", "é款")]
    public void WindowsUnsafeStyleNamesOnlyChangePublicDirectoryLabel(string input, string expected)
    {
        using var fixture = new Fixture();
        var batch = fixture.Batch(unified: true, styles: [(input, "")]);
        var target = Assert.Single(RenderStyleOutputPlanner.Plan(batch));
        Assert.Equal(expected, target.DirectoryName);
        Assert.Equal(fixture.Input, target.SourceDirectory);
        Assert.Equal(input.Trim(), batch.Styles[0].DefaultStyleCode);
    }

    [Fact]
    public void LongUnicodeNamesUseWholeTextElementsAndDisambiguateCaseAndNormalizationCollisions()
    {
        using var fixture = new Fixture();
        var prefix = new string('款', 95);
        var batch = fixture.Batch(unified: true, styles: [(prefix + "👩🏽‍💻A", "a"), (prefix + "👩🏽‍💻B", "b"),
            ("ABC", "c"), ("abc", "d"), ("é", "e"), ("e\u0301", "f")]);
        var targets = RenderStyleOutputPlanner.Plan(batch);
        Assert.Equal(prefix + "-01", targets[0].DirectoryName);
        Assert.Equal(prefix + "-02", targets[1].DirectoryName);
        Assert.Equal("ABC-01", targets[2].DirectoryName);
        Assert.Equal("abc-02", targets[3].DirectoryName);
        Assert.Equal("é-01", targets[4].DirectoryName);
        Assert.Equal("é-02", targets[5].DirectoryName);
        Assert.Equal(6, targets.Select(target => target.DirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(targets, target => Assert.DoesNotContain(Rune.ReplacementChar.ToString(), target.DirectoryName, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData(@"C:relative")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\?\C:\output")]
    [InlineData(@"\\.\C:\output")]
    [InlineData(@"C:\output:stream")]
    [InlineData(@"C:\output\..\other")]
    [InlineData(@"C:\output\.\other")]
    [InlineData(@"C:\output.\other")]
    [InlineData(@"C:\output \other")]
    [InlineData(@"C:\output\\other")]
    [InlineData(@"C:\output\NUL.txt")]
    [InlineData(@"C:\output\COM²")]
    [InlineData(@"C:\output\*")]
    [InlineData("C:\\output\" & calc.exe")]
    public void UnsafeRootsAreRejectedBeforeFilesystemAccess(string root)
    {
        using var fixture = new Fixture();
        var batch = fixture.Batch(unified: true, publicRoot: root);
        var before = fixture.Inventory();
        var error = Assert.Throws<RenderOutputTargetException>(() => RenderStyleOutputPlanner.Preflight(batch, default));
        Assert.Equal("RENDER_OUTPUT_DIRECTORY_INVALID", error.Code);
        Assert.Equal(before, fixture.Inventory());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingFileOrDirectoryIsConflictNotAdoptedMergedOrRenamed(bool file)
    {
        using var fixture = new Fixture();
        var batch = fixture.Batch();
        var target = Assert.Single(RenderStyleOutputPlanner.Plan(batch));
        if (file) File.WriteAllText(target.FinalDirectory, "old output");
        else
        {
            Directory.CreateDirectory(target.FinalDirectory);
            File.WriteAllText(Path.Combine(target.FinalDirectory, "keep.jpg"), "old output");
        }
        var before = fixture.Inventory();
        var error = Assert.Throws<RenderOutputTargetException>(() => RenderStyleOutputPlanner.Preflight(batch, default));
        Assert.Equal("RENDER_OUTPUT_DIRECTORY_CONFLICT", error.Code);
        Assert.Equal("style_0", error.StyleId);
        Assert.Equal(before, fixture.Inventory());
        // A failed preflight must not leak a source/ancestor pin.
        Directory.Move(fixture.Input, fixture.Input + "-moved");
    }

    [Fact]
    public void UnregisteredStagingDirectoryIsConflictEvenWithConvincingMarker()
    {
        using var fixture = new Fixture();
        var batch = fixture.Batch();
        var target = Assert.Single(RenderStyleOutputPlanner.Plan(batch));
        Directory.CreateDirectory(target.StagingDirectory);
        File.WriteAllText(Path.Combine(target.StagingDirectory, "commit.marker"), batch.BatchId);
        var before = fixture.Inventory();
        Assert.Equal("RENDER_OUTPUT_DIRECTORY_CONFLICT",
            Assert.Throws<RenderOutputTargetException>(() => RenderStyleOutputPlanner.Preflight(batch, default)).Code);
        Assert.Equal(before, fixture.Inventory());
    }

    [Fact]
    public void LaterStyleConflictReleasesAllEarlierLeasesAndCreatesNothing()
    {
        using var fixture = new Fixture();
        var batch = fixture.Batch(styles: [("A", "a"), ("B", "b")]);
        var targets = RenderStyleOutputPlanner.Plan(batch);
        Directory.CreateDirectory(targets[1].FinalDirectory);
        var before = fixture.Inventory();
        Assert.Equal("style_1", Assert.Throws<RenderOutputTargetException>(() => RenderStyleOutputPlanner.Preflight(batch, default)).StyleId);
        Assert.Equal(before, fixture.Inventory());
        Directory.Move(targets[0].SourceDirectory, targets[0].SourceDirectory + "-released");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingSourceOrPublicRootIsReportedAndNotRecreated(bool source)
    {
        using var fixture = new Fixture();
        var batch = fixture.Batch(unified: true);
        var missing = source ? fixture.Input : fixture.Public;
        Directory.Move(missing, missing + "-offline");
        var error = Assert.Throws<RenderOutputTargetException>(() => RenderStyleOutputPlanner.Preflight(batch, default));
        Assert.Equal("RENDER_OUTPUT_DIRECTORY_UNAVAILABLE", error.Code);
        Assert.False(Directory.Exists(missing));
        // Failure opening the source releases the already-open public root.
        if (source) Directory.Move(fixture.Public, fixture.Public + "-released");
    }

    [Theory]
    [InlineData("same-source")]
    [InlineData("source-parent")]
    [InlineData("evidence")]
    [InlineData("evidence-parent")]
    [InlineData("evidence-child")]
    [InlineData("evidence-drive-root")]
    public void TargetCannotReplaceASourceOrOverlapInternalEvidence(string scenario)
    {
        using var fixture = new Fixture();
        var publicRoot = fixture.Public;
        var name = "款名";
        var evidence = fixture.Evidence;
        if (scenario == "same-source") { publicRoot = Path.GetDirectoryName(fixture.Input)!; name = Path.GetFileName(fixture.Input); }
        if (scenario == "source-parent") { publicRoot = Path.GetDirectoryName(Path.GetDirectoryName(fixture.Input)!)!; name = Path.GetFileName(Path.GetDirectoryName(fixture.Input)!); }
        if (scenario == "evidence") evidence = Path.Combine(publicRoot, name);
        if (scenario == "evidence-parent") evidence = Path.Combine(publicRoot, name, "internal");
        if (scenario == "evidence-child") evidence = publicRoot;
        if (scenario == "evidence-drive-root") evidence = Path.GetPathRoot(publicRoot)!;
        var batch = fixture.Batch(unified: true, publicRoot: publicRoot, evidenceRoot: evidence, styles: [(name, "")]);
        Assert.Equal(scenario.StartsWith("source", StringComparison.Ordinal) || scenario == "same-source"
                ? "RENDER_OUTPUT_SOURCE_DIRECTORY_OVERLAP" : "RENDER_OUTPUT_EVIDENCE_DIRECTORY_OVERLAP",
            Assert.Throws<RenderOutputTargetException>(() => RenderStyleOutputPlanner.Plan(batch)).Code);
    }

    [Theory]
    [InlineData("deep/a")]
    [InlineData("A.")]
    [InlineData("A ")]
    public void InvalidFrozenStyleMappingDoesNotGuessOrWalkDeeper(string relative)
    {
        using var fixture = new Fixture();
        var batch = fixture.Batch(styles: [("A", "a"), ("B", "b")]);
        batch = WithStyles(batch, [StyleDraft.Create("style_0", "A", relative, 0, ["asset_0"]), batch.Styles[1]]);
        Assert.Throws<RenderOutputTargetException>(() => RenderStyleOutputPlanner.Plan(batch));
    }

    [Fact]
    public void SameCaseInsensitiveSourceCannotBeClaimedByTwoStyles()
    {
        using var fixture = new Fixture();
        var batch = fixture.Batch(styles: [("A", "a"), ("B", "b")]);
        batch = WithStyles(batch, [batch.Styles[0], StyleDraft.Create("style_1", "B", "A", 1, ["asset_1"])]);
        Assert.Equal("RENDER_OUTPUT_SOURCE_MAPPING_INVALID",
            Assert.Throws<RenderOutputTargetException>(() => RenderStyleOutputPlanner.Plan(batch)).Code);
    }

    [Fact]
    public void PreflightPinsSourcesAndParentsButStillDetectsNewLeafConflict()
    {
        using var fixture = new Fixture();
        var batch = fixture.Batch(unified: true);
        var preflight = RenderStyleOutputPlanner.Preflight(batch, default);
        try
        {
            var target = Assert.Single(preflight.Targets);
            Assert.Throws<IOException>(() => Directory.Move(fixture.Input, fixture.Input + "-swapped"));
            Assert.Throws<IOException>(() => Directory.Move(fixture.Public, fixture.Public + "-swapped"));
            var ancestor = Path.GetDirectoryName(fixture.Input)!;
            Assert.Throws<IOException>(() => Directory.Move(ancestor, ancestor + "-swapped"));
            Directory.CreateDirectory(target.FinalDirectory);
            Assert.Equal("RENDER_OUTPUT_DIRECTORY_CONFLICT",
                Assert.Throws<RenderOutputTargetException>(() => preflight.RevalidateAvailable(default)).Code);
        }
        finally { preflight.Dispose(); }
        preflight.Dispose();
        Directory.Move(fixture.Input, fixture.Input + "-released");
        Directory.Move(fixture.Public, fixture.Public + "-released");
        Assert.Throws<ObjectDisposedException>(() => preflight.GetIdentity(fixture.Input));
        Assert.Throws<ObjectDisposedException>(() => preflight.RevalidateAvailable(default));
    }

    [Fact]
    public void DirectoryIdentityDetectsReplacementAfterLeaseReleased()
    {
        using var fixture = new Fixture();
        RenderOutputDirectoryIdentity original;
        using (var lease = RenderOutputDirectoryLease.Open(fixture.Input))
        {
            original = lease.Identity;
            lease.Verify();
        }
        Directory.Move(fixture.Input, fixture.Input + "-original");
        Directory.CreateDirectory(fixture.Input);
        using var replacement = RenderOutputDirectoryLease.Open(fixture.Input);
        Assert.NotEqual(original, replacement.Identity);
        Assert.Equal("source media sentinel", File.ReadAllText(Path.Combine(fixture.Input + "-original", "source.jpg")));
    }

    [Fact]
    public void DirectoryLeaseRejectsAFileAndCancellationDoesNotCreateOrLeak()
    {
        using var fixture = new Fixture();
        Assert.Equal("RENDER_OUTPUT_DIRECTORY_IDENTITY_INVALID",
            Assert.Throws<RenderOutputTargetException>(() => RenderOutputDirectoryLease.Open(Path.Combine(fixture.Input, "source.jpg"))).Code);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => RenderStyleOutputPlanner.Preflight(fixture.Batch(), cancellation.Token));
        using (var preflight = RenderStyleOutputPlanner.Preflight(fixture.Batch(), default))
            Assert.Throws<OperationCanceledException>(() => preflight.RevalidateAvailable(cancellation.Token));
        Directory.Move(fixture.Input, fixture.Input + "-released");
    }

    [Fact]
    public void LegacyBatchAndUnconfirmedInputCannotEnterNewPublicationLayout()
    {
        using var fixture = new Fixture();
        var batch = fixture.Batch();
        var legacy = BatchDraft.Restore(batch.BatchId, batch.ScanStatus, batch.InputType, batch.InputRoot, batch.OutputRoot,
            batch.TemplateSnapshot, batch.SelectionMode, batch.IncludeEditablePsd, batch.Styles, batch.Assets,
            batch.ManifestReference, batch.ManifestDigest, batch.BlockReason, batch.CreatedAt, batch.UpdatedAt);
        Assert.Equal("RENDER_OUTPUT_SELECTION_NOT_CONFIRMED",
            Assert.Throws<RenderOutputTargetException>(() => RenderStyleOutputPlanner.Plan(legacy)).Code);
        var draft = BatchDraft.Create(batch.BatchId, batch.InputRoot, batch.OutputRoot, batch.TemplateSnapshot,
            batch.SelectionMode, false, batch.CreatedAt, batch.OutputSelection);
        Assert.Equal("RENDER_OUTPUT_SELECTION_NOT_CONFIRMED",
            Assert.Throws<RenderOutputTargetException>(() => RenderStyleOutputPlanner.Plan(draft)).Code);
    }

    [Fact]
    public void StageIsStableForBatchAndStyleButDifferentForAnotherBatch()
    {
        using var fixture = new Fixture();
        var batch = fixture.Batch();
        var first = Assert.Single(RenderStyleOutputPlanner.Plan(batch));
        Assert.Equal(first, Assert.Single(RenderStyleOutputPlanner.Plan(batch)));
        var second = Assert.Single(RenderStyleOutputPlanner.Plan(fixture.Batch(batchId: "batch_other")));
        Assert.Equal(first.FinalDirectory, second.FinalDirectory);
        Assert.NotEqual(first.StagingDirectory, second.StagingDirectory);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("public")]
    [InlineData("ancestor")]
    [InlineData("evidence")]
    public async Task RealJunctionIsRejectedWithoutFollowingOrWritingIt(string location)
    {
        using var fixture = new Fixture();
        var batch = fixture.Batch(unified: true);
        var link = location switch
        {
            "source" => fixture.Input,
            "public" => fixture.Public,
            "ancestor" => Path.GetDirectoryName(fixture.Input)!,
            _ => fixture.Evidence
        };
        var original = link + "-original";
        if (Directory.Exists(link)) Directory.Move(link, original);
        else Directory.CreateDirectory(original);
        await CreateJunction(link, original);
        try
        {
            var before = fixture.Inventory();
            Assert.Equal("RENDER_OUTPUT_DIRECTORY_IDENTITY_INVALID",
                Assert.Throws<RenderOutputTargetException>(() =>
                { using var unexpected = RenderStyleOutputPlanner.Preflight(batch, default); }).Code);
            Assert.Equal(before, fixture.Inventory());
            Assert.False(Directory.Exists(Path.Combine(fixture.Public, "真实款名")));
        }
        finally { Directory.Delete(link); }
        Directory.Move(original, link); // Failure released all ancestor/leaf pins.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingJunctionAtFinalOrStageIsAConflictNotAdopted(bool stage)
    {
        using var fixture = new Fixture();
        var batch = fixture.Batch();
        var target = Assert.Single(RenderStyleOutputPlanner.Plan(batch));
        var link = stage ? target.StagingDirectory : target.FinalDirectory;
        await CreateJunction(link, fixture.Public);
        try
        {
            Assert.Equal("RENDER_OUTPUT_DIRECTORY_CONFLICT",
                Assert.Throws<RenderOutputTargetException>(() => RenderStyleOutputPlanner.Preflight(batch, default)).Code);
            Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Public));
        }
        finally { Directory.Delete(link); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealAddSubdirectoryDenialBlocksBothModesAndReleasesEarlierLeases(bool unified)
    {
        using var fixture = new Fixture();
        var batch = fixture.Batch(unified: unified, styles: [("A", "a"), ("B", "b")]);
        var target = RenderStyleOutputPlanner.Plan(batch)[1];
        using (var denial = new DirectoryAccessDenial(target.ParentDirectory, FileSystemRights.CreateDirectories))
        {
            // The OS really denies the operation, not an injected exception.
            Assert.Throws<UnauthorizedAccessException>(() => Directory.CreateDirectory(Path.Combine(target.ParentDirectory, "probe")));
            var before = fixture.Inventory();
            var error = Assert.Throws<RenderOutputTargetException>(() => RenderStyleOutputPlanner.Preflight(batch, default));
            Assert.Equal("RENDER_OUTPUT_DIRECTORY_ACCESS_DENIED", error.Code);
            Assert.Equal(unified ? "style_0" : "style_1", error.StyleId);
            Assert.Equal(before, fixture.Inventory());
        }
        Directory.Move(fixture.Input, fixture.Input + "-released");
        Directory.Move(fixture.Public, fixture.Public + "-released");
    }

    [Fact]
    public void UnifiedSourceNeedsReadAccessButNotCreateDirectoryAccess()
    {
        using var fixture = new Fixture();
        var batch = fixture.Batch(unified: true);
        using var denial = new DirectoryAccessDenial(fixture.Input, FileSystemRights.CreateDirectories);
        using var preflight = RenderStyleOutputPlanner.Preflight(batch, default);
        preflight.RevalidateAvailable(default);
    }

    [Fact]
    public void MissingEvidenceLeafStillPinsItsExistingAncestor()
    {
        using var fixture = new Fixture();
        var evidenceParent = Directory.CreateDirectory(fixture.Evidence).FullName;
        var batch = fixture.Batch(unified: true, evidenceRoot: Path.Combine(evidenceParent, "not-created", "evidence"));
        using (var preflight = RenderStyleOutputPlanner.Preflight(batch, default))
        {
            Assert.Throws<IOException>(() => Directory.Move(evidenceParent, evidenceParent + "-swapped"));
            Assert.False(Directory.Exists(Path.Combine(evidenceParent, "not-created")));
        }
        Directory.Move(evidenceParent, evidenceParent + "-released");
    }

    [Theory]
    [InlineData("source")]
    [InlineData("public")]
    [InlineData("evidence")]
    public void RealWindowsShortAliasCannotBypassSourceOrEvidenceBoundaries(string location)
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Evidence);
        var longPath = location switch { "source" => fixture.Input, "public" => fixture.Public, _ => fixture.Evidence };
        var buffer = new char[32768];
        var length = GetShortPathNameW(longPath, buffer, (uint)buffer.Length);
        Assert.InRange(length, 1u, 32767u);
        var alias = new string(buffer, 0, checked((int)length));
        // This Windows acceptance case requires a real alias, not an invented '~1' name.
        Assert.False(alias.Equals(longPath, StringComparison.OrdinalIgnoreCase), "No 8.3 alias available; no alias rejection evidence.");
        var batch = fixture.Batch(unified: true, publicRoot: location == "public" ? alias : null,
            evidenceRoot: location == "evidence" ? alias : null);
        if (location == "source") batch = BatchDraft.Restore(batch.BatchId, batch.ScanStatus, batch.InputType, alias,
            batch.OutputRoot, batch.TemplateSnapshot, batch.SelectionMode, batch.IncludeEditablePsd, batch.Styles, batch.Assets,
            batch.ManifestReference, batch.ManifestDigest, batch.BlockReason, batch.CreatedAt, batch.UpdatedAt, batch.OutputSelection);
        var before = fixture.Inventory();
        var error = Assert.Throws<RenderOutputTargetException>(() =>
        { using var unexpected = RenderStyleOutputPlanner.Preflight(batch, default); });
        Assert.Equal("RENDER_OUTPUT_DIRECTORY_INVALID", error.Code);
        Assert.Equal(before, fixture.Inventory());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string path, [Out] char[] shortPath, uint length);

    private static async Task CreateJunction(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            $"/d /c mklink /J \"{link}\" \"{target}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, process.ExitCode);
        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
    }

    // Only changes one fresh, test-owned directory, and restores the exact original DACL.
    private sealed class DirectoryAccessDenial : IDisposable
    {
        private readonly DirectoryInfo _directory;
        private readonly DirectorySecurity _original;
        internal DirectoryAccessDenial(string path, FileSystemRights rights)
        {
            _directory = new(path);
            _original = _directory.GetAccessControl(AccessControlSections.Access);
            var denied = _directory.GetAccessControl(AccessControlSections.Access);
            using var identity = WindowsIdentity.GetCurrent();
            denied.AddAccessRule(new FileSystemAccessRule(identity.User!, rights, AccessControlType.Deny));
            _directory.SetAccessControl(denied);
        }
        public void Dispose()
        {
            var restored = new DirectorySecurity();
            restored.SetSecurityDescriptorBinaryForm(_original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            _directory.SetAccessControl(restored);
            var actual = _directory.GetAccessControl(AccessControlSections.Access);
            Assert.Equal(_original.AreAccessRulesProtected, actual.AreAccessRulesProtected);
            Assert.Equal(DaclBytes(_original), DaclBytes(actual));

            static byte[] DaclBytes(DirectorySecurity security)
            {
                var acl = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl!;
                var bytes = new byte[acl.BinaryLength]; acl.GetBinaryForm(bytes, 0); return bytes;
            }
        }
    }

    private static BatchDraft WithStyles(BatchDraft batch, IEnumerable<StyleDraft> styles) => BatchDraft.Restore(
        batch.BatchId, batch.ScanStatus, batch.InputType, batch.InputRoot, batch.OutputRoot, batch.TemplateSnapshot,
        batch.SelectionMode, batch.IncludeEditablePsd, styles, batch.Assets, batch.ManifestReference,
        batch.ManifestDigest, batch.BlockReason, batch.CreatedAt, batch.UpdatedAt, batch.OutputSelection);

    private sealed class Fixture : IDisposable
    {
        private readonly TestDirectory _directory = new();
        internal string Input { get; }
        internal string Public { get; }
        internal string Evidence { get; }

        internal Fixture()
        {
            Input = Directory.CreateDirectory(Path.Combine(_directory.Path, "款号不猜", "新建文件夹")).FullName;
            Public = Directory.CreateDirectory(Path.Combine(_directory.Path, "public")).FullName;
            Evidence = Path.Combine(_directory.Path, "private");
            File.WriteAllText(Path.Combine(Input, "source.jpg"), "source media sentinel");
        }

        internal BatchDraft Batch(bool unified = false, string? publicRoot = null, string? evidenceRoot = null,
            (string Name, string Relative)[]? styles = null, string batchId = "batch_test")
        {
            styles ??= [("真实款名", "")];
            var now = DateTimeOffset.UtcNow;
            var snapshot = BatchTemplateSnapshot.Restore("snapshot_" + batchId, batchId, "revision",
                [new("page", 0, TemplatePageContentType.NormalComposition, TemplateGenerationMode.OncePerProduct, null, "resource", [])],
                "sha256:" + new string('a', 64), now);
            var selection = unified ? BatchOutputSelection.Create(BatchOutputMode.UnifiedDirectory, publicRoot ?? Public)
                : BatchOutputSelection.Create(BatchOutputMode.StyleSourceDirectories);
            var items = styles.Select((style, index) => StyleDraft.Create("style_" + index, style.Name, style.Relative, index, ["asset_" + index])).ToArray();
            foreach (var item in items) Directory.CreateDirectory(Path.Combine(Input, item.SourceDirectoryRelativePath));
            var assets = items.Select((style, index) => SourceAssetMetadata.CreateReadable("asset_" + index, style.StyleId,
                (style.SourceDirectoryRelativePath.Length == 0 ? "" : style.SourceDirectoryRelativePath + "/") + "source.jpg",
                "source.jpg", 128, new string('b', 64), 10, 10, SourceImageFormat.Jpeg, "image/jpeg"));
            return BatchDraft.Create(batchId, Input, evidenceRoot ?? Evidence, snapshot, BatchSelectionMode.Automatic, false, now, selection)
                .BeginScanning(now).MarkScanReady(styles.Length == 1 && styles[0].Relative.Length == 0
                    ? BatchInputType.SingleStyle : BatchInputType.StyleDirectoryBatch, items, assets,
                    "batches/" + batchId + "/manifest.json", "sha256:" + new string('c', 64), now);
        }

        internal string[] Inventory() => Directory.GetFileSystemEntries(_directory.Path, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(_directory.Path, path)).Order(StringComparer.Ordinal).ToArray();

        public void Dispose() => _directory.Dispose();
    }
}
