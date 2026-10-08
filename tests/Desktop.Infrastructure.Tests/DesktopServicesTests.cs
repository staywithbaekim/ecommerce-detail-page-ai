using System.Reflection;
using Desktop.Application.Ai;
using Desktop.Application.Diagnostics;
using Desktop.Application.Rendering;
using Desktop.Contracts.Validation;
using Desktop.Infrastructure.Ai;
using Desktop.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Desktop.Infrastructure.Tests;

public sealed class DesktopServicesTests
{
    [Fact]
    public async Task ProductionPublisherAndCoordinatorRecoverPublicFilesWithoutPhotoshopSelectionOrNewExecutions()
    {
        using var fixture = new RenderStyleOutputRegistryTests.Fixture();
        await using (var seed = await fixture.Open())
            await RenderStyleOutputPublicationTests.Prepare(fixture, seed);
        var services = new ServiceCollection(); services.AddDesktopFoundation(fixture.Options);
        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<SqliteDesktopStore>(); await store.InitializeAsync();
        Assert.Same(provider.GetRequiredService<Desktop.Infrastructure.Bridge.RenderStyleOutputPublisher>(),
            provider.GetRequiredService<IRenderStyleOutputPublisher>());
        var before = (await store.LoadRenderBatchAsync("batch_test", default))!;
        var result = await provider.GetRequiredService<IRenderBatchWorkflow>().RunAsync("batch_test", default);
        Assert.Equal(RenderBatchStatus.CompletedWithIssues, result.Status);
        Assert.Equal(before.Pages, result.Pages);
        Assert.Equal("2", await fixture.Scalar("SELECT count(*) FROM render_output_publications WHERE state='Committed';"));
        var reader = provider.GetRequiredService<IRenderBatchOutputReader>();
        Assert.NotNull(await reader.GetStyleOutputDirectoryAsync("batch_test", "style_a", default));
        Assert.NotNull(await reader.GetStyleOutputDirectoryAsync("batch_test", "style_b", default));
        Assert.Null(provider.GetRequiredService<IPhotoshopConnectionCheckPort>().Latest);
    }

    [Fact]
    public async Task RepairAndRenderingPortsShareOneCoordinatorAndAiAdmissionInstanceAndDisposeSafely()
    {
        using var directory = new TestDirectory();
        var services = new ServiceCollection();
        services.AddDesktopFoundation(new DesktopDataOptions(directory.Path));
        await using var provider = services.BuildServiceProvider();
        var coordinator = provider.GetRequiredService<IRenderBatchWorkflow>();
        Assert.Same(coordinator, provider.GetRequiredService<ISizeTablePageRepairWorkflow>());
        Assert.Same(coordinator, provider.GetRequiredService<RenderBatchCoordinator>());
        Assert.Same(provider.GetRequiredService<IAiBatchAnalysisWorkflow>(), provider.GetRequiredService<ISizeTableRepairAdmission>());
        Assert.NotNull(provider.GetRequiredService<ISizeTablePageRepairPreparer>());
        Assert.IsType<Desktop.Infrastructure.Diagnostics.LocalDiagnosticPackageExporter>(provider.GetRequiredService<IDiagnosticPackageExporter>());
        Assert.IsType<Desktop.Infrastructure.Diagnostics.WindowsPhotoshopEnvironmentProbe>(provider.GetRequiredService<IPhotoshopEnvironmentProbe>());
        var connectionCheck = provider.GetRequiredService<IPhotoshopConnectionCheckPort>();
        Assert.Same(provider.GetRequiredService<IPhotoshopPairingPort>(), connectionCheck);
        Assert.Null(connectionCheck.Latest);
        Assert.Same(provider.GetRequiredService<Desktop.Infrastructure.Bridge.PhotoshopHostSelectionService>(),
            provider.GetRequiredService<IPhotoshopHostSelectionPort>());
        var owners = provider.GetRequiredService<Desktop.Infrastructure.Bridge.BridgeSessionOwnerFactory>();
        Assert.Equal("PHOTOSHOP_HOST_SELECTION_USE_ASYNC_OWNER",
            Assert.Throws<InvalidOperationException>(() => owners.CreateTaskOwner("task_must_not_bypass_selection")).Message);
    }
    [Fact]
    public async Task ReferenceLearningUploadAdmissionUsesDurableConsentStore()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(directory.Path));
        var services = new ServiceCollection();
        services.AddDesktopFoundation(new DesktopDataOptions(directory.Path));
        var descriptor = Assert.Single(
            services,
            item => item.ServiceType == typeof(IAiUploadAdmissionPort));
        var provider = new FixedServiceProvider(new Dictionary<Type, object>
        {
            [typeof(SqliteDesktopStore)] = store
        });

        Assert.Same(store, descriptor.ImplementationFactory!(provider));
    }

    [Fact]
    public void ProductionAiHttpClientDoesNotPreemptAdapterTimeout()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai01-production-di-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var services = new ServiceCollection();
            services.AddDesktopFoundation(new DesktopDataOptions(root));
            var logger = new NullLogger();
            services.AddSingleton<IRedactedLogger>(logger);
            using var provider = services.BuildServiceProvider();
            var adapter = Assert.IsType<VolcArkAiAdapter>(provider.GetRequiredService<IAiServicePort>());
            var field = typeof(VolcArkAiAdapter).GetField(
                "_httpClient",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var client = Assert.IsType<HttpClient>(field!.GetValue(adapter));
            var loggerField = typeof(VolcArkAiAdapter).GetField(
                "_transportLogger",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
            Assert.Same(logger, loggerField!.GetValue(adapter));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class FixedServiceProvider(IReadOnlyDictionary<Type, object> services) : IServiceProvider
    {
        public object? GetService(Type serviceType) => services.GetValueOrDefault(serviceType);
    }

    private sealed class EmptyCredentialSource : IAiCredentialSource
    {
        public ValueTask<string?> GetCredentialAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>(null);
    }

    private sealed class EmptyCopySource : IAiAnalysisCopySource
    {
        public Task<AiAnalysisCopy> OpenAsync(string analysisCopyReference, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReleaseAsync(string analysisCopyReference, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class NullLogger : IRedactedLogger
    {
        public void Information(string eventCode, IReadOnlyDictionary<string, object?> properties)
        {
        }

        public void LogError(
            string eventCode,
            string errorCode,
            IReadOnlyDictionary<string, object?> properties)
        {
        }
    }
}
