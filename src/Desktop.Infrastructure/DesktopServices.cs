using Desktop.Application.Ai;
using Desktop.Application.Diagnostics;
using Desktop.Application.Events;
using Desktop.Application.Files;
using Desktop.Application.Psd;
using Desktop.Application.Rendering;
using Desktop.Application.Tasks;
using Desktop.Contracts.Validation;
using Desktop.Domain.Tasks;
using Desktop.Infrastructure.Ai;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Diagnostics;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Persistence;
using Desktop.Infrastructure.Psd;
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

        services.AddSingleton<SqliteDesktopStore>();
        services.AddSingleton<ITaskEventStore>(provider => provider.GetRequiredService<SqliteDesktopStore>());
        services.AddSingleton<ICheckpointStore>(provider => provider.GetRequiredService<SqliteDesktopStore>());
        services.AddSingleton<IAppInstanceStore>(provider => provider.GetRequiredService<SqliteDesktopStore>());
        services.AddSingleton<TaskCoordinator>();

        services.AddSingleton<IPathPolicy>(_ => new LocalAppPathPolicy(options.RootDirectory));
        services.AddSingleton<IFileStore, AtomicFileStore>();
        services.AddSingleton<IManifestStore, JsonManifestStore>();
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
