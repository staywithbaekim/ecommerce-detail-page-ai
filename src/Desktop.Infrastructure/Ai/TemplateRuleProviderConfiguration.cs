using System.Text.Json;
using Desktop.Application.Ai;
using Desktop.Application.Templates;
using Desktop.Contracts.Ai;

namespace Desktop.Infrastructure.Ai;

/// <summary>Explicit local candidate selection, not an automatic retry/fallback list.</summary>
public sealed record TemplateRuleProviderConfiguration(bool UseOpenAiCandidate = false)
{
    public static TemplateRuleProviderConfiguration Read(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "ai", "template-rule-provider.json");
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 1024 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("AI_PROVIDER_CONFIGURATION_INVALID");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1
            || !root.TryGetProperty("provider", out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("AI_PROVIDER_CONFIGURATION_INVALID");
        return value.GetString() switch
        {
            "volc-ark" => new(),
            "openai-candidate" => new(true),
            _ => throw new InvalidDataException("AI_PROVIDER_CONFIGURATION_INVALID")
        };
    }
}

// The existing port remains the only application dependency. All other tasks
// retain the established provider; candidate support is not misrepresented as
// image/size/reference parity. In-flight requests never change implementation.
internal sealed class TemplateRuleConfiguredAiPort(IAiServicePort analysis, IAiServicePort rules) : IAiServicePort
{
    public AiTaskProviderIdentity TemplateRulesIdentity => rules.TemplateRulesIdentity;
    public Task<TemplateRuleAiExecution> InterpretTemplateRulesAsync(TemplateRuleAiRequest request, CancellationToken token) =>
        // The current budget ledger uses the approved Ark CNY tariff. Do not silently
        // apply that price to a different supplier or label a candidate production-ready.
        Task.FromResult(new TemplateRuleAiExecution(AiProviderOutcome.TerminalError, null, null,
            new("AI_PROVIDER_PRODUCTION_VALIDATION_REQUIRED", false, "provider",
                "OpenAI 文字适配器仍为候选；须完成该供应商计费配置与真实请求验收后才能用于生产。当前未发送请求。")));
    public Task<AiAnalysisExecutionResult> AnalyzeAsync(AiAnalysisRequestV1 request, CancellationToken token) =>
        analysis.AnalyzeAsync(request, token);
    public Task<ReferenceLearningAiExecutionResult> AnalyzeReferenceLearningAsync(ReferenceLearningAiRequestV2 request, CancellationToken token) =>
        analysis.AnalyzeReferenceLearningAsync(request, token);
}
