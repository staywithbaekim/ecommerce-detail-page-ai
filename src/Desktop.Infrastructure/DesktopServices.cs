using Desktop.Application.Ai;
using Desktop.Application.Diagnostics;
using Desktop.Application.Events;
using Desktop.Application.Files;
using Desktop.Application.Inputs;
using Desktop.Application.Psd;
using Desktop.Application.Rendering;
using Desktop.Application.SizeTables;
using Desktop.Application.Tasks;
using Desktop.Application.Templates;
using Desktop.Contracts.Validation;
using Desktop.Domain.Tasks;
using Desktop.Infrastructure.Ai;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Diagnostics;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Inputs;
using Desktop.Infrastructure.Persistence;
using Desktop.Infrastructure.Psd;
using Desktop.Infrastructure.SizeTables;
using Desktop.Infrastructure.Templates;
using Microsoft.Extensions.DependencyInjection;

namespace Desktop.Infrastructure;

public static class DesktopServices
{
    public static IServiceCollection AddDesktopFoundation(
        this IServiceCollection services,
        DesktopDataOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.AddSingleton<ApplicationEventBus>();
        services.AddSingleton<IApplicationEventBus>(provider => provider.GetRequiredService<ApplicationEventBus>());
        services.AddSingleton<JsonSchemaContractValidator>();
        services.AddSingleton<IJsonContractValidator>(provider => provider.GetRequiredService<JsonSchemaContractValidator>());
        services.AddSingleton<IRenderPlanValidator, RenderPlanValidator>();
        services.AddSingleton<ITemplateRenderPlanBuilder, TemplateRenderPlanBuilder>();
        services.AddSingleton<ISizeTableRenderPlanBuilder, SizeTableRenderPlanBuilder>();
        services.AddSingleton<IPhotoshopAvailabilityPort, UnavailablePhotoshopAvailabilityPort>();
        services.AddSingleton<IAiRenderPlanFactory, AiRenderPlanFactory>();
        services.AddSingleton<ISizeTableRepository, SizeTableFileRepository>();
        services.AddSingleton<ISizeTableEditingWorkflow, SizeTableEditingWorkflow>();
        services.AddSingleton<ISizeTableRasterizer, WpfSizeTableRasterizer>();

        services.AddSingleton<SqliteDesktopStore>();
        services.AddSingleton<ITaskEventStore>(provider => provider.GetRequiredService<SqliteDesktopStore>());
        services.AddSingleton<ICheckpointStore>(provider => provider.GetRequiredService<SqliteDesktopStore>());
        services.AddSingleton<IAppInstanceStore>(provider => provider.GetRequiredService<SqliteDesktopStore>());
        services.AddSingleton<ITemplateRepository>(provider => provider.GetRequiredService<SqliteDesktopStore>());
        services.AddSingleton<IBatchSnapshotRepository>(provider => provider.GetRequiredService<SqliteDesktopStore>());
        services.AddSingleton<IAiAnalysisStore>(provider => provider.GetRequiredService<SqliteDesktopStore>());
        services.AddSingleton<IAiAnalysisSchedulingStateStore>(provider => provider.GetRequiredService<SqliteDesktopStore>());
        services.AddSingleton<IAiAnalysisCheckpointDatabase>(provider => provider.GetRequiredService<SqliteDesktopStore>());
        services.AddSingleton<IAiAnalysisStateManifestStore, AiAnalysisStateManifestStore>();
        services.AddSingleton<IAiAnalysisCheckpointPort, AiAnalysisCheckpointCoordinator>();
        services.AddSingleton<ITemplateResourceRepository>(provider => provider.GetRequiredService<SqliteDesktopStore>());
        services.AddSingleton<IFileInventory, ReadOnlyFileInventory>();
        services.AddSingleton<IAssetMetadataReader, StreamingAssetMetadataReader>();
        services.AddSingleton<IAssetManifestStore, AssetManifestFileStore>();
        services.AddSingleton<IInputCommitJournalStore, JsonInputCommitJournalStore>();
        services.AddSingleton<IBatchRepository, RecoveringBatchRepository>();
        services.AddSingleton<IBatchScanCommitter, SqliteBatchScanCommitter>();
        services.AddSingleton(new BatchScanOptions());
        services.AddSingleton<BatchInputService>();
        services.AddSingleton<BatchInputWorkflowFacade>();
        services.AddSingleton<TaskCoordinator>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<TemplatePageImportService>();
        services.AddSingleton<ITemplateWorkspaceService, TemplateWorkspaceService>();
        services.AddSingleton<ICalibrationAssetSourcePort, ReadOnlyCalibrationAssetSourcePort>();
        services.AddSingleton<ITemplateCalibrationService, TemplateCalibrationService>();
        services.AddSingleton<ICalibrationStateRepository>(provider => provider.GetRequiredService<SqliteDesktopStore>());
        services.AddSingleton<ITemplatePreviewRepository>(provider => provider.GetRequiredService<SqliteDesktopStore>());
        services.AddSingleton<ITemplatePackagePort, ZipTemplatePackagePort>();
        services.AddSingleton<ITemplatePackageService, TemplatePackageService>();
        services.AddSingleton<ITemplateOwnedStorage, TemplateOwnedStorage>();

        services.AddSingleton<IReferencePhotoFingerprintPort, ReferencePhotoFingerprintPort>();
        services.AddSingleton<IReferenceLearningSessionStore, JsonReferenceLearningSessionStore>();
        services.AddSingleton<IReferenceLearningAiCacheStore, JsonReferenceLearningAiCacheStore>();
        services.AddSingleton<IReferenceLearningAiService, ReferenceLearningAiService>();
        services.AddSingleton<IReferenceLearningService, ReferenceLearningService>();
        services.AddSingleton<IReferenceLearningCommitService, ReferenceLearningCommitService>();

        services.AddSingleton<IPathPolicy>(_ => new LocalAppPathPolicy(options.RootDirectory));
        services.AddSingleton<IFileStore, AtomicFileStore>();
        services.AddSingleton<IManifestStore, JsonManifestStore>();
        services.AddSingleton<ITemplateSourceFilePort, TemplateSourceFilePort>();
        services.AddSingleton(PsdInspectionWorkerOptions.ForInstalledApp());
        services.AddSingleton<IPsdInspectionPort, PsdInspectionWorkerClient>();
        services.AddSingleton<IRedactedLogger>(provider => new RedactingFileLogger(
            provider.GetRequiredService<IPathPolicy>(),
            Path.Combine("logs", "desktop-v1.jsonl")));

        services.AddSingleton<IBridgeClient, MockBridgeClient>();
        services.AddSingleton<IAiAnalyzer, MockAiAnalyzer>();
        services.AddSingleton<WindowsCredentialSource>();
        services.AddSingleton<IAiCredentialSource>(provider =>
            provider.GetRequiredService<WindowsCredentialSource>());
        services.AddSingleton<IAiCredentialManagementPort>(provider =>
            provider.GetRequiredService<WindowsCredentialSource>());
        services.AddSingleton<IAiUsageRecorder, InMemoryAiUsageRecorder>();
        services.AddSingleton(new AiAnalysisCopyEncodingOptions());
        services.AddSingleton<IAiAnalysisCopySource>(provider =>
            new FileAiAnalysisCopySource(provider.GetRequiredService<IPathPolicy>()));
        services.AddSingleton<FileAiAnalysisCopyPreparer>(provider =>
            new FileAiAnalysisCopyPreparer(
                provider.GetRequiredService<IPathPolicy>(),
                provider.GetRequiredService<IFileStore>(),
                provider.GetRequiredService<AiAnalysisCopyEncodingOptions>()));
        services.AddSingleton<IAiAnalysisCopyPreparer>(provider => provider.GetRequiredService<FileAiAnalysisCopyPreparer>());
        services.AddSingleton<IAiAnalysisCopyLeaseManager>(provider => provider.GetRequiredService<FileAiAnalysisCopyPreparer>());
        services.AddSingleton<IAiAnalysisResultStore>(provider =>
            new FileAiAnalysisResultStore(
                provider.GetRequiredService<IFileStore>(),
                provider.GetRequiredService<IJsonContractValidator>()));
        services.AddSingleton<IConsentPort>(provider =>
            provider.GetRequiredService<SqliteDesktopStore>());
        services.AddSingleton<IAiUploadAdmissionPort>(provider =>
            provider.GetRequiredService<SqliteDesktopStore>());
        services.AddSingleton<VolcArkAiOptions>();
        services.AddSingleton<IAiServicePort>(provider => new VolcArkAiAdapter(
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
            provider.GetRequiredService<IAiCredentialSource>(),
            provider.GetRequiredService<IAiAnalysisCopySource>(),
            provider.GetRequiredService<IJsonContractValidator>(),
            provider.GetRequiredService<VolcArkAiOptions>(),
            provider.GetService<IRedactedLogger>()));
        services.AddSingleton<IAiAnalysisSchedulerFactory, AiAnalysisSchedulerFactory>();
        services.AddSingleton<IAiBatchAnalysisWorkflow, AiBatchAnalysisWorkflow>();
        return services;
    }
}
