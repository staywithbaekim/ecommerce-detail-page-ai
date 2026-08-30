using System.Reflection;
using Desktop.Application.Ai;
using Desktop.Contracts.Validation;
using Desktop.Infrastructure.Ai;
using Desktop.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Desktop.Infrastructure.Tests;

public sealed class DesktopServicesTests
{
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
            var provider = new FixedServiceProvider(new Dictionary<Type, object>
            {
                [typeof(IAiCredentialSource)] = new EmptyCredentialSource(),
                [typeof(IAiAnalysisCopySource)] = new EmptyCopySource(),
                [typeof(IJsonContractValidator)] = new JsonSchemaContractValidator(),
                [typeof(VolcArkAiOptions)] = new VolcArkAiOptions()
            });
            var adapter = Assert.IsType<VolcArkAiAdapter>(descriptor.ImplementationFactory!(provider));
            var field = typeof(VolcArkAiAdapter).GetField(
                "_httpClient",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var client = Assert.IsType<HttpClient>(field!.GetValue(adapter));

            Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
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
}
