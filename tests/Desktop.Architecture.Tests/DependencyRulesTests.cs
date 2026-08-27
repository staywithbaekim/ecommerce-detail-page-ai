using System.Reflection;
using Desktop.Application.Ai;
using Desktop.Application.Rendering;
using Desktop.Application.Tasks;
using Desktop.App;
using Desktop.Contracts.Validation;
using Desktop.Domain.Tasks;
using Desktop.Infrastructure.Ai;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Persistence;

namespace Desktop.Architecture.Tests;

public sealed class DependencyRulesTests
{
    [Fact]
    public void DomainHasNoFrameworkOrOuterLayerDependencies()
    {
        var references = ReferenceNames(typeof(TaskState).Assembly);

        Assert.DoesNotContain(references, IsForbiddenDomainReference);
    }

    [Fact]
    public void ContractsHaveNoWpfSqliteOrAdapterDependency()
    {
        var references = ReferenceNames(typeof(JsonSchemaContractValidator).Assembly);

        Assert.DoesNotContain(references, name =>
            name.StartsWith("Presentation", StringComparison.Ordinal)
            || name == "WindowsBase"
            || name == "Microsoft.Data.Sqlite"
            || name == "Desktop.Infrastructure"
            || name == "Desktop.App");
    }

    [Fact]
    public void ApplicationDependsInwardAndNotOnInfrastructure()
    {
        var references = ReferenceNames(typeof(TaskCoordinator).Assembly);

        Assert.Contains("Desktop.Domain", references);
        Assert.Contains("Desktop.Contracts", references);
        Assert.DoesNotContain("Desktop.Infrastructure", references);
        Assert.DoesNotContain("Desktop.App", references);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", references);
        Assert.DoesNotContain(references, name => name.StartsWith("Presentation", StringComparison.Ordinal));
    }

    [Fact]
    public void PresentationUsesApplicationAndInfrastructureButNotDomainDirectly()
    {
        var references = ReferenceNames(typeof(Desktop.App.App).Assembly);

        Assert.Contains("Desktop.Application", references);
        Assert.Contains("Desktop.Infrastructure", references);
        Assert.DoesNotContain("Desktop.Domain", references);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", references);
    }

    [Fact]
    public void InfrastructureImplementsPortsWithoutProviderTypesInContracts()
    {
        Assert.Contains(typeof(ITaskEventStore), typeof(SqliteDesktopStore).GetInterfaces());
        Assert.Contains(typeof(IBridgeClient), typeof(MockBridgeClient).GetInterfaces());
        Assert.Contains(typeof(IAiAnalyzer), typeof(MockAiAnalyzer).GetInterfaces());
        Assert.Contains(typeof(IRenderPlanValidator), typeof(RenderPlanValidator).GetInterfaces());
    }

    private static HashSet<string> ReferenceNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

    private static bool IsForbiddenDomainReference(string name) =>
        name.StartsWith("Presentation", StringComparison.Ordinal)
        || name is "WindowsBase"
            or "System.Net.Http"
            or "Microsoft.Data.Sqlite"
            or "Desktop.Application"
            or "Desktop.Contracts"
            or "Desktop.Infrastructure"
            or "Desktop.App";
}
