using Desktop.Application.Psd;
using Desktop.Application.Templates;

namespace Desktop.Application.Tests;

public sealed class TemplatePageImportServiceTests
{
    private const string FileHash =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string StructureHash =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public async Task DuplicateRequiresExplicitDecisionAfterOwnedCopyIsStaged()
    {
        var existing = Resource("resource_existing");
        var files = new FakeSourceFiles();
        var repository = new FakeResourceRepository(existing);
        var service = CreateService(files, repository, PsdInspectionResult.Success(Inspection()));

        var result = await service.ImportAsync(Request("command_01", "resource_new"));

        Assert.Equal(TemplatePageImportStatus.DuplicateReviewRequired, result.Status);
        Assert.Equal("TPL_DUPLICATE_REVIEW_REQUIRED", result.Error!.Code);
        Assert.Equal(existing.ResourceId, Assert.Single(result.DuplicateCandidates).ResourceId);
        Assert.Equal(1, files.CopyCount);
        Assert.Equal(0, files.DeleteCount);
        Assert.Equal(0, repository.SaveCount);
    }

    [Fact]
    public async Task SuccessfulImportIsRecordedOnlyAfterInspectionAndRetryIsIdempotent()
    {
        var files = new FakeSourceFiles();
        var repository = new FakeResourceRepository();
        var service = CreateService(files, repository, PsdInspectionResult.Success(Inspection()));
        var request = Request("command_01", "resource_new");

        var first = await service.ImportAsync(request);
        var repeated = await service.ImportAsync(request);

        Assert.True(first.IsSuccess);
        Assert.Equal(TemplatePageImportStatus.Imported, first.Status);
        Assert.True(repeated.Idempotent);
        Assert.Equal(first.Resource!.ResourceId, repeated.Resource!.ResourceId);
        Assert.Equal(1, files.CopyCount);
        Assert.Equal(1, repository.SaveCount);
        Assert.Equal(0, files.DeleteCount);
    }

    [Fact]
    public async Task InspectionFailureDeletesOwnedCopyAndCreatesNoResource()
    {
        var files = new FakeSourceFiles();
        var repository = new FakeResourceRepository();
        var service = CreateService(
            files,
            repository,
            PsdInspectionResult.Failure(new PsdInspectionError(
                "TPL_PSD_UNSUPPORTED_STRUCTURE",
                "Unsupported.",
                "Choose another PSD.",
                Retryable: false)));

        var result = await service.ImportAsync(Request("command_01", "resource_new"));

        Assert.Equal(TemplatePageImportStatus.Failed, result.Status);
        Assert.Equal("TPL_PSD_UNSUPPORTED_STRUCTURE", result.Error!.Code);
        Assert.Equal(2, files.DeleteCount);
        Assert.Equal(0, repository.SaveCount);
    }

    [Fact]
    public async Task InspectionHashMismatchDeletesOwnedCopyAndBlocksImport()
    {
        var files = new FakeSourceFiles();
        var repository = new FakeResourceRepository();
        var service = CreateService(
            files,
            repository,
            PsdInspectionResult.Success(Inspection(new string('f', 64))));

        var result = await service.ImportAsync(Request("command_01", "resource_new"));

        Assert.Equal("TPL_SOURCE_CHANGED_DURING_COPY", result.Error!.Code);
        Assert.Equal(2, files.DeleteCount);
        Assert.Equal(0, repository.SaveCount);
    }

    [Fact]
    public async Task ReuseExistingRecordsCommandAndDeletesOnlyTheRedundantOwnedCopy()
    {
        var existing = Resource("resource_existing");
        var files = new FakeSourceFiles();
        var repository = new FakeResourceRepository(existing);
        var service = CreateService(files, repository, PsdInspectionResult.Success(Inspection()));
        var request = Request(
            "command_01",
            "resource_new",
            TemplateDuplicateDecision.ReuseExisting,
            existing.ResourceId);

        var result = await service.ImportAsync(request);

        Assert.Equal(TemplatePageImportStatus.ReusedExisting, result.Status);
        Assert.Equal(existing.ResourceId, result.Resource!.ResourceId);
        Assert.Equal(1, files.CopyCount);
        Assert.Equal(1, files.DeleteCount);
        Assert.Equal(1, repository.RecordSelectionCount);
    }

    [Fact]
    public async Task AutoReuseDraftDuplicateIsIdempotentAcrossRetry()
    {
        var existing = Resource("resource_existing");
        var files = new FakeSourceFiles();
        var repository = new FakeResourceRepository(existing);
        var service = CreateService(files, repository, PsdInspectionResult.Success(Inspection()));
        var request = Request(
            "command_auto_reuse",
            "resource_new",
            TemplateDuplicateDecision.AutoReuseDraftDuplicates);

        var first = await service.ImportAsync(request);
        var repeated = await service.ImportAsync(request);

        Assert.Equal(TemplatePageImportStatus.ReusedExisting, first.Status);
        Assert.Equal(TemplatePageImportStatus.ReusedExisting, repeated.Status);
        Assert.True(repeated.Idempotent);
        Assert.Equal(existing.ResourceId, repeated.Resource!.ResourceId);
        Assert.Equal(1, files.CopyCount);
        Assert.Equal(1, files.DeleteCount);
        Assert.Equal(1, repository.RecordSelectionCount);
    }

    [Fact]
    public async Task CancelDeletesOwnedCopyAndDoesNotRecordAResource()
    {
        var existing = Resource("resource_existing");
        var files = new FakeSourceFiles();
        var repository = new FakeResourceRepository(existing);
        var service = CreateService(files, repository, PsdInspectionResult.Success(Inspection()));

        var result = await service.ImportAsync(Request(
            "command_01",
            "resource_new",
            TemplateDuplicateDecision.Cancel));

        Assert.Equal(TemplatePageImportStatus.Cancelled, result.Status);
        Assert.Null(result.Error);
        Assert.Equal(1, files.CopyCount);
        Assert.Equal(1, files.DeleteCount);
        Assert.Equal(0, repository.SaveCount);
        Assert.Equal(0, repository.RecordSelectionCount);
    }

    [Fact]
    public async Task InvalidDuplicateSelectionDeletesOwnedCopyAndCreatesNoResource()
    {
        var existing = Resource("resource_existing");
        var files = new FakeSourceFiles();
        var repository = new FakeResourceRepository(existing);
        var service = CreateService(files, repository, PsdInspectionResult.Success(Inspection()));

        var result = await service.ImportAsync(Request(
            "command_01",
            "resource_new",
            TemplateDuplicateDecision.ReuseExisting,
            "resource_missing"));

        Assert.Equal(TemplatePageImportStatus.Failed, result.Status);
        Assert.Equal("TPL_DUPLICATE_SELECTION_INVALID", result.Error!.Code);
        Assert.Equal(1, files.CopyCount);
        Assert.Equal(1, files.DeleteCount);
        Assert.Equal(0, repository.SaveCount);
        Assert.Equal(0, repository.RecordSelectionCount);
    }

    [Fact]
    public async Task ReusingCommandForDifferentResourceIsRejectedBeforeReadingSource()
    {
        var existing = Resource("resource_existing");
        var files = new FakeSourceFiles();
        var repository = new FakeResourceRepository();
        await repository.SaveImportedAsync("command_01", existing, CancellationToken.None);
        var service = CreateService(files, repository, PsdInspectionResult.Success(Inspection()));

        var result = await service.ImportAsync(Request("command_01", "resource_different"));

        Assert.Equal("TPL_IMPORT_COMMAND_CONFLICT", result.Error!.Code);
        Assert.Equal(0, files.FingerprintCount);
        Assert.Equal(0, files.CopyCount);
    }

    [Fact]
    public async Task DuplicateLookupFailureDeletesOwnedCopyAndCreatesNoResource()
    {
        var files = new FakeSourceFiles();
        var repository = new FakeResourceRepository { FailDuplicateLookup = true };
        var service = CreateService(files, repository, PsdInspectionResult.Success(Inspection()));

        var result = await service.ImportAsync(Request("command_01", "resource_new"));

        Assert.Equal(TemplatePageImportStatus.Failed, result.Status);
        Assert.Equal("TPL_REVISION_COMMIT_FAILED", result.Error!.Code);
        Assert.Equal(1, files.CopyCount);
        Assert.Equal(1, files.DeleteCount);
        Assert.Equal(0, repository.SaveCount);
    }

    private static TemplatePageImportService CreateService(
        FakeSourceFiles files,
        FakeResourceRepository repository,
        PsdInspectionResult inspection) => new(
            files,
            new FakeInspectionPort(inspection),
            repository,
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero)));

    private static TemplatePageImportRequest Request(
        string commandId,
        string resourceId,
        TemplateDuplicateDecision decision = TemplateDuplicateDecision.NotSpecified,
        string? selectedDuplicateResourceId = null) => new(
            commandId,
            "group_01",
            "draft_01",
            resourceId,
            @"C:\readonly\source.psd",
            decision,
            selectedDuplicateResourceId);

    private static ImportedTemplateResource Resource(string resourceId) => new(
        resourceId,
        "group_01",
        "draft_01",
        $"templates/groups/group_01/drafts/draft_01/staging/{resourceId}/template.psd",
        3,
        FileHash,
        StructureHash,
        Inspection(),
        new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero));

    private static PsdInspectionDocument Inspection(string? hash = null) => new(
        hash ?? FileHash,
        3,
        new PsdCanvasInspection(10, 10, 8, "RGB"),
        0,
        0,
        0,
        0,
        0,
        StructureHash,
        new PsdInspectionTiming(1, 1, 2),
        1024,
        null,
        []);

    private sealed class FakeInspectionPort(PsdInspectionResult result) : IPsdInspectionPort
    {
        public Task<PsdInspectionResult> InspectAsync(
            PsdInspectionRequest request,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class FakeSourceFiles : ITemplateSourceFilePort
    {
        public int FingerprintCount { get; private set; }

        public int CopyCount { get; private set; }

        public int DeleteCount { get; private set; }

        public Task<SourceFingerprintResult> FingerprintAsync(
            string sourcePsdPath,
            CancellationToken cancellationToken)
        {
            FingerprintCount++;
            return Task.FromResult(SourceFingerprintResult.Success(
                new TemplateSourceFingerprint(3, FileHash)));
        }

        public Task<OwnedTemplateCopyResult> CopyVerifiedAsync(
            string sourcePsdPath,
            TemplateSourceFingerprint expectedSource,
            string destinationRelativePath,
            CancellationToken cancellationToken)
        {
            CopyCount++;
            return Task.FromResult(OwnedTemplateCopyResult.Success(new OwnedTemplateCopy(
                destinationRelativePath,
                expectedSource.Length,
                expectedSource.Sha256)));
        }

        public Task<bool> DeleteOwnedCopyAsync(
            string internalRelativePath,
            CancellationToken cancellationToken)
        {
            DeleteCount++;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeResourceRepository(params ImportedTemplateResource[] initial)
        : ITemplateResourceRepository
    {
        private readonly Dictionary<string, ImportedTemplateResource> _commands = new(StringComparer.Ordinal);
        private readonly List<ImportedTemplateResource> _resources = [.. initial];

        public int SaveCount { get; private set; }

        public int RecordSelectionCount { get; private set; }

        public bool FailDuplicateLookup { get; init; }

        public Task<ImportedTemplateResource?> GetByResourceIdAsync(
            string resourceId,
            CancellationToken cancellationToken) => Task.FromResult(
                _resources.SingleOrDefault(resource => string.Equals(
                    resource.ResourceId,
                    resourceId,
                    StringComparison.Ordinal)));

        public Task<ImportedTemplateResource?> GetByCommandIdAsync(
            string commandId,
            CancellationToken cancellationToken)
        {
            _commands.TryGetValue(commandId, out var resource);
            return Task.FromResult(resource);
        }

        public Task<IReadOnlyList<ImportedTemplateResource>> FindBySha256Async(
            string sha256,
            CancellationToken cancellationToken)
        {
            if (FailDuplicateLookup)
            {
                throw new IOException("Injected duplicate lookup failure.");
            }

            return Task.FromResult<IReadOnlyList<ImportedTemplateResource>>(
                _resources.Where(resource => string.Equals(
                    resource.Sha256,
                    sha256,
                    StringComparison.OrdinalIgnoreCase)).ToArray());
        }

        public Task<ImportedTemplateResource> SaveImportedAsync(
            string commandId,
            ImportedTemplateResource resource,
            CancellationToken cancellationToken)
        {
            SaveCount++;
            _resources.Add(resource);
            _commands.Add(commandId, resource);
            return Task.FromResult(resource);
        }

        public Task<ImportedTemplateResource> RecordExistingSelectionAsync(
            string commandId,
            ImportedTemplateResource resource,
            CancellationToken cancellationToken)
        {
            RecordSelectionCount++;
            _commands.Add(commandId, resource);
            return Task.FromResult(resource);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
