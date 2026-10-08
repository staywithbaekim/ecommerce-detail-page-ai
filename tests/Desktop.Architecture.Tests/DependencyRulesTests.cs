using System.Reflection;
using Desktop.Application.Ai;
using Desktop.Application.Rendering;
using Desktop.Application.SizeTables;
using Desktop.Application.Tasks;
using Desktop.Application.Templates;
using Desktop.App;
using Desktop.Contracts.Validation;
using Desktop.Domain.Tasks;
using Desktop.Domain.Templates;
using Desktop.Infrastructure.Ai;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Persistence;

namespace Desktop.Architecture.Tests;

public sealed class DependencyRulesTests
{
    [Fact]
    public void ShellCommandsAreLimitedToNavigationAndCrossPageHostContracts()
    {
        var shellType = typeof(MainWindowViewModel);
        var hostCommands = shellType.GetInterfaces().SelectMany(type => type.GetProperties())
            .Where(property => typeof(System.Windows.Input.ICommand).IsAssignableFrom(property.PropertyType))
            .Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        hostCommands.Add(nameof(MainWindowViewModel.OpenBatchInputCommand));
        hostCommands.Add(nameof(MainWindowViewModel.RestoreBatchInputCommand));
        hostCommands.Add(nameof(MainWindowViewModel.RefreshTasksCommand));
        hostCommands.Add(nameof(MainWindowViewModel.RestoreTaskCommand));
        hostCommands.Add(nameof(MainWindowViewModel.ShowTasksCommand));
        Assert.All(shellType.GetProperties().Where(property =>
                typeof(System.Windows.Input.ICommand).IsAssignableFrom(property.PropertyType)),
            property => Assert.Contains(property.Name, hostCommands));
        Assert.DoesNotContain(shellType.GetEvents(),
            eventInfo => eventInfo.Name == nameof(AiCredentialViewModel.AiCredentialInputCleared));
    }

    [Fact]
    public void DomainHasNoFrameworkOrOuterLayerDependencies()
    {
        var references = ReferenceNames(typeof(TaskState).Assembly);

        Assert.DoesNotContain(references, IsForbiddenDomainReference);
        Assert.Same(typeof(TaskState).Assembly, typeof(TemplateGroup).Assembly);
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
        Assert.Contains(typeof(ITemplateRepository), typeof(SqliteDesktopStore).GetInterfaces());
        Assert.Contains(typeof(IBatchSnapshotRepository), typeof(SqliteDesktopStore).GetInterfaces());
    }

    private static HashSet<string> ReferenceNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void BatchProjectionOnlyDependsOnReadPortAndReceivesFactsWithoutRuntimeServices()
    {
        var constructor = Assert.Single(typeof(AiBatchProjectionService).GetConstructors());
        Assert.Equal(typeof(IAiAnalysisResultReader), Assert.Single(constructor.GetParameters()).ParameterType);
        Assert.All(typeof(AiBatchProjectionService).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => Assert.Equal(typeof(IAiAnalysisResultReader), field.FieldType));
        Assert.Equal(new[] { nameof(IAiAnalysisResultReader.LoadSessionAsync), nameof(IAiAnalysisResultReader.LoadStyleResultsAsync) },
            typeof(IAiAnalysisResultReader).GetMethods().Select(method => method.Name).Order(StringComparer.Ordinal));
        Assert.All(typeof(AiBatchProjectionSnapshot).GetProperties(), property =>
            Assert.True(property.PropertyType.Assembly != typeof(AiBatchProjectionService).Assembly
                || property.PropertyType.IsEnum,
                $"Snapshot must carry facts, not Application services: {property.Name}"));
    }

    [Fact]
    public void RenderPreparationUsesOnlyResourcePortsAndReceivesNoRuntimeController()
    {
        Type[] allowedPorts =
        [
            typeof(IAiAnalysisResultReader), typeof(ITemplateResourceRepository), typeof(IAiRenderPlanFactory),
            typeof(ISizeTableRepository), typeof(ISizeTableRasterizer),
            // ADR-035 E3: durable facts/revision ports and renderer-neutral validation, never a runtime controller.
            typeof(ITemplateInstanceProductionEvidenceStore), typeof(ITemplateInstanceProjectionSnapshotStore),
            typeof(ITemplateRepository), typeof(IRenderPlanValidator)
        ];
        Assert.All(Assert.Single(typeof(AiRenderPreparationService).GetConstructors()).GetParameters(),
            parameter => Assert.Contains(parameter.ParameterType, allowedPorts));
        Assert.All(typeof(AiRenderPreparationService).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => Assert.Contains(field.FieldType, allowedPorts));
        Assert.All(typeof(AiRenderPreparationSnapshot).GetProperties(), property =>
            Assert.True(property.PropertyType.Assembly != typeof(AiRenderPreparationService).Assembly
                || property.PropertyType == typeof(AiBatchAnalysisProjection),
                $"Snapshot must carry facts, not Application services: {property.Name}"));
    }

    [Fact]
    public void AnalysisRecoveryOwnsOnlyBatchRecoveryStateAndBorrowsScheduler()
    {
        Type[] dependencies = [typeof(string), typeof(AiAnalysisScheduler), typeof(IAiAnalysisCheckpointPort)];
        Assert.All(Assert.Single(typeof(AiAnalysisRecoveryService).GetConstructors()).GetParameters(),
            parameter => Assert.Contains(parameter.ParameterType, dependencies));
        Assert.All(typeof(AiAnalysisRecoveryService).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => Assert.True(dependencies.Contains(field.FieldType)
                || field.FieldType == typeof(SemaphoreSlim) || field.FieldType == typeof(bool),
                $"Recovery must not own admission or background processing: {field.Name}"));
        var property = Assert.Single(typeof(AiAnalysisRecoveryService).GetProperties());
        Assert.Equal(typeof(string), property.PropertyType);
        Assert.False(property.SetMethod?.IsPublic);
    }

    [Fact]
    public void BatchCommandsHideMutableRuntimeAndDoNotDependOnProjectionOrRenderingServices()
    {
        var fields = typeof(AiBatchCommandService).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.DoesNotContain(fields, field =>
            field.FieldType == typeof(AiBatchProjectionService)
            || field.FieldType == typeof(AiRenderPreparationService)
            || field.FieldType == typeof(Task)
            || field.FieldType == typeof(CancellationTokenSource));
        Assert.Empty(typeof(AiBatchCommandService).GetProperties());
        Assert.Equal(typeof(AiBatchProjectionSnapshot),
            typeof(AiBatchCommandService).GetMethod(nameof(AiBatchCommandService.CaptureSnapshot))!.ReturnType);
        Assert.DoesNotContain(typeof(AiBatchCommandService).GetMethods(BindingFlags.Instance | BindingFlags.Public),
            method => method.ReturnType == typeof(AiAnalysisScheduler));
    }

    [Fact]
    public void BatchBackgroundRunnerBorrowsOnlyBatchFactsSchedulerAndShutdownToken()
    {
        Type[] dependencies = [typeof(Desktop.Domain.Inputs.BatchDraft), typeof(TemplateRevision),
            typeof(AiAnalysisScheduler), typeof(CancellationToken)];
        Assert.All(Assert.Single(typeof(AiBatchBackgroundRunner).GetConstructors()).GetParameters(),
            parameter => Assert.Contains(parameter.ParameterType, dependencies));
        Assert.All(typeof(AiBatchBackgroundRunner).GetProperties(), property =>
            Assert.False(property.SetMethod?.IsPublic ?? false));
        Assert.DoesNotContain(typeof(AiBatchBackgroundRunner).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(AiBatchCommandService)
                || field.FieldType == typeof(AiBatchProjectionService)
                || field.FieldType == typeof(AiRenderPreparationService));
    }

    [Fact]
    public void AiAnalysisPageStateOwnsPresentationStateWithoutWorkflowDependency()
    {
        var stateType = typeof(MainWindowViewModel).Assembly.GetType("Desktop.App.AiAnalysisPageState")
            ?? throw new InvalidOperationException("AI analysis page state type not found.");
        Assert.Contains(stateType.GetProperties(), property => property.Name == "Styles");
        Assert.Contains(stateType.GetProperties(), property => property.Name == "StatusFilters");
        Assert.Contains(stateType.GetProperties(), property => property.Name == "RefreshTimer");
        Assert.DoesNotContain(stateType.GetProperties(), property =>
            property.PropertyType == typeof(IAiBatchAnalysisWorkflow));
        Assert.Contains(typeof(AiAnalysisViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == stateType);
        Assert.DoesNotContain(typeof(MainWindowViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == stateType
                || field.Name is "_currentBatchId" or "_aiRefreshTimer");
    }

    [Fact]
    public void MainWindowOwnsAiAnalysisCoordinatorInsteadOfConstructingCommandsInline()
    {
        var coordinatorType = typeof(MainWindowViewModel).Assembly.GetType("Desktop.App.AiAnalysisViewModel")
            ?? throw new InvalidOperationException("AI analysis coordinator type not found.");
        Assert.Contains(typeof(MainWindowViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == coordinatorType);
        Assert.Contains(coordinatorType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(IAiBatchAnalysisWorkflow));
        Assert.DoesNotContain(coordinatorType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(MainWindowViewModel));
        Assert.DoesNotContain(typeof(MainWindowViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(IAiBatchAnalysisWorkflow));
        Type[] ports = [typeof(IAiBatchAnalysisWorkflow), typeof(IAiAnalysisHost)];
        Assert.All(Assert.Single(coordinatorType.GetConstructors()).GetParameters(),
            parameter => Assert.Contains(parameter.ParameterType, ports));
        Assert.Contains(coordinatorType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic),
            method => method.Name == "InitializeCommands");
    }

    [Fact]
    public void ResultReviewPageOwnsPresentationStateWithoutWorkflowDependency()
    {
        Assert.Contains(typeof(ResultReviewPageViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(ResultReviewViewModel));
        Assert.Contains(typeof(ResultReviewViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(ResultReviewPageState));
        Assert.Equal(new[] { typeof(IResultReviewHost), typeof(IRenderBatchWorkflow), typeof(IRenderBatchOutputReader) },
            Assert.Single(typeof(ResultReviewViewModel).GetConstructors()).GetParameters().Select(parameter => parameter.ParameterType));
        Assert.DoesNotContain(typeof(ResultReviewViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(MainWindowViewModel)
                || field.FieldType == typeof(IAiBatchAnalysisWorkflow));
        Assert.Contains(typeof(ResultReviewPageState).GetProperties(),
            property => property.Name == nameof(ResultReviewPageState.ResultStyleGroups));
        Assert.Contains(typeof(ResultReviewPageState).GetProperties(),
            property => property.Name == nameof(ResultReviewPageState.SelectedResultPage));
        Assert.DoesNotContain(typeof(ResultReviewPageState).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(IAiBatchAnalysisWorkflow)
                || field.FieldType == typeof(CancellationTokenSource));
    }

    [Fact]
    public void SettingsPageOwnsCredentialPresentationStateWithoutCredentialPort()
    {
        Assert.Contains(typeof(SettingsPageViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(AiCredentialViewModel));
        Assert.Contains(typeof(AiCredentialViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(SettingsPageState));
        Assert.Contains(typeof(SettingsPageState).GetProperties(),
            property => property.Name == nameof(SettingsPageState.AiCredentialStatusText));
        Assert.DoesNotContain(typeof(SettingsPageState).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(IAiCredentialManagementPort));
    }

    [Fact]
    public void BatchInputPageOwnsPresentationStateWithoutWorkflowOrPersistencePorts()
    {
        Assert.Contains(typeof(BatchInputPageViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(BatchInputViewModel));
        Assert.Contains(typeof(BatchInputViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(BatchInputPageState));
        Assert.Contains(typeof(BatchInputPageState).GetProperties(),
            property => property.Name == nameof(BatchInputPageState.BatchPages));
        Assert.Contains(typeof(BatchInputPageState).GetProperties(),
            property => property.Name == nameof(BatchInputPageState.BatchStatusState));
        Assert.DoesNotContain(typeof(BatchInputPageState).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(IBatchInputWorkflow)
                || field.FieldType == typeof(IFolderPicker)
                || field.FieldType == typeof(SemaphoreSlim)
                || field.FieldType == typeof(CancellationTokenSource));
    }

    [Fact]
    public void TemplateWorkspacePageOwnsViewportStateWithoutTemplateWorkflowDependencies()
    {
        Assert.Contains(typeof(TemplateWorkspacePageViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(TemplateWorkspaceViewModel));
        Assert.Contains(typeof(TemplateWorkspaceViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(TemplateWorkspacePageState));
        Assert.DoesNotContain(typeof(TemplateWorkspaceViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(MainWindowViewModel));
        Type[] workflowPorts = [typeof(ITemplateWorkspaceService), typeof(ITemplateCalibrationService),
            typeof(ITemplatePackageService), typeof(IReferenceLearningService),
            typeof(IReferenceLearningCommitService), typeof(IReferenceLearningAiService), typeof(IFolderPicker)];
        Assert.DoesNotContain(typeof(MainWindowViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => workflowPorts.Contains(field.FieldType));
        Assert.Contains(typeof(TemplateWorkspacePageState).GetProperties(),
            property => property.Name == nameof(TemplateWorkspacePageState.TemplateBoardZoom));
        Assert.Contains(typeof(TemplateWorkspacePageState).GetProperties(),
            property => property.Name == nameof(TemplateWorkspacePageState.CalibrationViewportZoom));
        Assert.Contains(typeof(TemplateWorkspacePageState).GetProperties(),
            property => property.Name == nameof(TemplateWorkspacePageState.IsTemplateDialogVisible));
        Assert.Contains(typeof(TemplateWorkspacePageState).GetProperties(),
            property => property.Name == nameof(TemplateWorkspacePageState.TemplateStatusMessage));
        Assert.Contains(typeof(TemplateWorkspacePageState).GetProperties(),
            property => property.Name == nameof(TemplateWorkspacePageState.SelectedTemplatePage));
        Assert.DoesNotContain(typeof(TemplateWorkspacePageState).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(ITemplateWorkspaceService)
                || field.FieldType == typeof(ITemplateCalibrationService)
                || field.FieldType == typeof(ITemplatePackageService)
                || field.FieldType == typeof(IReferenceLearningService)
                || field.FieldType == typeof(IReferenceLearningCommitService)
                || field.FieldType == typeof(IReferenceLearningAiService)
                || field.FieldType == typeof(CancellationTokenSource)
                || field.FieldType == typeof(SemaphoreSlim));
    }

    [Fact]
    public void SizeTableEditorOwnsCommandsThroughApplicationPortAndNarrowPageContext()
    {
        Type[] dependencies = [typeof(ISizeTableEditingWorkflow), typeof(ISizeTableEditorHost),
            typeof(IRenderBatchWorkflow), typeof(ISizeTablePageRepairWorkflow)];
        Assert.All(Assert.Single(typeof(SizeTableEditorViewModel).GetConstructors()).GetParameters(),
            parameter => Assert.Contains(parameter.ParameterType, dependencies));
        Assert.False(typeof(PageViewModel).IsAssignableFrom(typeof(SizeTableEditorViewModel)));
        Assert.DoesNotContain(typeof(SizeTableEditorViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(MainWindowViewModel));
        Assert.DoesNotContain(typeof(MainWindowViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(ISizeTableEditingWorkflow));
        Assert.DoesNotContain(typeof(SizeTableEditorState).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(ISizeTableEditingWorkflow)
                || field.FieldType == typeof(ISizeTableEditorHost));
    }

    [Fact]
    public void SettingsCredentialsOwnPortAndCommandsWithoutShellOrAiWorkflow()
    {
        Assert.Equal(typeof(IAiCredentialManagementPort),
            Assert.Single(Assert.Single(typeof(AiCredentialViewModel).GetConstructors()).GetParameters()).ParameterType);
        Assert.False(typeof(PageViewModel).IsAssignableFrom(typeof(AiCredentialViewModel)));
        Assert.DoesNotContain(typeof(AiCredentialViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(MainWindowViewModel)
                || field.FieldType == typeof(IAiBatchAnalysisWorkflow));
        Assert.DoesNotContain(typeof(MainWindowViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(IAiCredentialManagementPort));
    }

    [Fact]
    public void BatchInputOwnsDraftAndScanLifecycleWithoutAiWorkflowOrShell()
    {
        Type[] ports = [typeof(IFolderPicker), typeof(IBatchInputWorkflow),
            typeof(ITemplateWorkspaceService), typeof(IBatchInputHost)];
        Assert.All(Assert.Single(typeof(BatchInputViewModel).GetConstructors()).GetParameters(),
            parameter => Assert.Contains(parameter.ParameterType, ports));
        Assert.False(typeof(PageViewModel).IsAssignableFrom(typeof(BatchInputViewModel)));
        Assert.DoesNotContain(typeof(BatchInputViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(MainWindowViewModel)
                || field.FieldType == typeof(IAiBatchAnalysisWorkflow));
        Assert.DoesNotContain(typeof(MainWindowViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(IBatchInputWorkflow));
    }

    [Theory]
    [InlineData(typeof(BatchInputPageViewModel))]
    [InlineData(typeof(ResultReviewPageViewModel))]
    [InlineData(typeof(TemplateWorkspacePageViewModel))]
    [InlineData(typeof(SettingsPageViewModel))]
    [InlineData(typeof(AiStyleAnalysisViewModel))]
    [InlineData(typeof(AiPageAnalysisViewModel))]
    public void PageBindingsAndItemsDoNotDependOnConcreteShellOrDynamicFallback(Type pageType)
    {
        Assert.False(typeof(System.Dynamic.DynamicObject).IsAssignableFrom(pageType));
        Assert.False(typeof(System.ComponentModel.ICustomTypeDescriptor).IsAssignableFrom(pageType));
        Assert.DoesNotContain(pageType.GetConstructors().SelectMany(constructor => constructor.GetParameters()),
            parameter => parameter.ParameterType == typeof(MainWindowViewModel));
        for (Type? type = pageType; type is not null; type = type.BaseType)
        {
            Assert.DoesNotContain(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                field => field.FieldType == typeof(MainWindowViewModel));
            Assert.DoesNotContain(type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                property => property.PropertyType == typeof(MainWindowViewModel));
        }
    }

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
