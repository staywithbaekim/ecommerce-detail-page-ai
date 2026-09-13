using System.Reflection;
using Desktop.Application.Ai;
using Desktop.Application.Diagnostics;
using Desktop.Contracts.Validation;
using Desktop.Infrastructure.Ai;
using Desktop.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Desktop.Infrastructure.Tests;

public sealed class DesktopServicesTests
{
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
            var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IAiServicePort));
            var logger = new NullLogger();
            var provider = new FixedServiceProvider(new Dictionary<Type, object>
            {
                [typeof(IAiCredentialSource)] = new EmptyCredentialSource(),
                [typeof(IAiAnalysisCopySource)] = new EmptyCopySource(),
                [typeof(IJsonContractValidator)] = new JsonSchemaContractValidator(),
                [typeof(VolcArkAiOptions)] = new VolcArkAiOptions(),
                [typeof(IRedactedLogger)] = logger
            });
            var adapter = Assert.IsType<VolcArkAiAdapter>(descriptor.ImplementationFactory!(provider));
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
