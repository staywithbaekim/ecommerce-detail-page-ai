using Desktop.Application.Ai;
using Desktop.Application.Diagnostics;
using Desktop.Application.Events;
using Desktop.Application.Files;
using Desktop.Application.Inputs;
using Desktop.Application.Psd;
using Desktop.Application.Rendering;
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

        services.AddSingleton<SqliteDesktopStore>();
        services.AddSingleton<ITaskEventStore>(provider => provider.GetRequiredService<SqliteDesktopStore>());
        services.AddSingleton<ICheckpointStore>(provider => provider.GetRequiredService<SqliteDesktopStore>());
        services.AddSingleton<IAppInstanceStore>(provider => provider.GetRequiredService<SqliteDesktopStore>());
        services.AddSingleton<ITemplateRepository>(provider => provider.GetRequiredService<SqliteDesktopStore>());
        services.AddSingleton<IBatchSnapshotRepository>(provider => provider.GetRequiredService<SqliteDesktopStore>());
        services.AddSingleton<IAiAnalysisStore>(provider => provider.GetRequiredService<SqliteDesktopStore>());
        services.AddSingleton<IAiAnalysisStateManifestStore, AiAnalysisStateManifestStore>();
        services.AddSingleton<ITemplateResourceRepository>(provider => provider.GetRequiredService<SqliteDesktopStore>());
        services.AddSingleton<IFileInventory, ReadOnlyFileInventory>();
        services.AddSingleton<IAssetMetadataReader, StreamingAssetMetadataReader>();
        services.AddSingleton<IAssetManifestStore, AssetManifestFileStore>();
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
        services.AddSingleton<IAiCredentialSource, NullAiCredentialSource>();
        services.AddSingleton<IAiUsageRecorder, InMemoryAiUsageRecorder>();
        return services;
    }
}
