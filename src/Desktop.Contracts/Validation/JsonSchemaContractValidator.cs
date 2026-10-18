using System.Reflection;
using System.Text.Json;
using Json.Schema;

namespace Desktop.Contracts.Validation;

public sealed class JsonSchemaContractValidator : IJsonContractDiagnosticValidator
{
    private static readonly Lazy<Dictionary<ContractDocumentKind, JsonSchema>> SchemaCache = new(CreateSchemas);
    private readonly Dictionary<ContractDocumentKind, JsonSchema> _schemas;

    public JsonSchemaContractValidator()
    {
        _schemas = SchemaCache.Value;
    }

    public ContractValidationResult Validate(ContractDocumentKind kind, string json)
        => ValidateWithDiagnostic(kind, json).Validation;

    public ContractValidationDiagnosticResult ValidateWithDiagnostic(ContractDocumentKind kind, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return DiagnosticFailure("CONTRACT_EMPTY", "请选择有效的合同数据后重试。", null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var instance = document.RootElement;

            var result = _schemas[kind].Evaluate(instance, new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = true
            });

            if (result.IsValid)
            {
                return new ContractValidationDiagnosticResult(ContractValidationResult.Success, null, null);
            }

            var firstFailure = FindFirstFailure(result);
            return DiagnosticFailure(
                "CONTRACT_SCHEMA_REJECTED",
                "合同结构不符合当前协议，请修正后重试。",
                firstFailure.Path,
                firstFailure.Keyword);
        }
        catch (Exception exception) when (exception is JsonException or JsonSchemaException)
        {
            return DiagnosticFailure("CONTRACT_INVALID_JSON", "合同数据无法解析，请修正后重试。", null, null);
        }
    }

    private static (string? Path, string? Keyword) FindFirstFailure(EvaluationResults result)
    {
        // Hierarchical output may put the useful error on a child node. Walk
        // depth-first and only expose the pointer/keyword, never the message.
        foreach (var detail in result.Details ?? [])
        {
            var failure = FindFirstFailure(detail);
            if (failure.Path is not null || failure.Keyword is not null)
            {
                return failure;
            }
        }

        if (result.Errors is { Count: > 0 } && !result.IsValid)
        {
            return (SanitizePointer(result.InstanceLocation.ToString()),
                SanitizeKeyword(result.Errors.Keys.FirstOrDefault()));
        }

        return !result.IsValid
            ? (SanitizePointer(result.InstanceLocation.ToString()), null)
            : (null, null);
    }

    private static string? SanitizePointer(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
        {
            return null;
        }

        // Json Pointer locations are structural. Keep only the bounded ASCII
        // form so an implementation-specific ToString cannot leak data.
        foreach (var character in value)
        {
            if (!(character is '/' or '~' or >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_' or '-' or '.'))
            {
                return null;
            }
        }

        return value;
    }

    private static string? SanitizeKeyword(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 64
        && value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')
            ? value
            : null;

    private static JsonSchema LoadSchema(string fileName)
    {
        var assembly = typeof(JsonSchemaContractValidator).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(fileName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("嵌入式合同 Schema 不可用。");
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    private static Dictionary<ContractDocumentKind, JsonSchema> CreateSchemas() => new()
    {
        [ContractDocumentKind.RenderPlan] = LoadSchema("render-plan-v1.schema.json"),
        [ContractDocumentKind.BridgeEnvelope] = LoadSchema("bridge-v1.schema.json"),
        [ContractDocumentKind.AdapterTransport] = LoadSchema("adapter-transport-v1.schema.json"),
        [ContractDocumentKind.TemplateRevisionManifest] = LoadSchema("template-revision-v1.schema.json"),
        [ContractDocumentKind.TemplatePackageManifest] = LoadSchema("template-package-v1.schema.json"),
        [ContractDocumentKind.TemplatePackageManifestV2] = LoadSchema("template-package-v2.schema.json"),
        [ContractDocumentKind.TemplatePackageManifestV3] = LoadSchema("template-package-v3.schema.json"),
        [ContractDocumentKind.TemplatePackageManifestV4] = LoadSchema("template-package-v4.schema.json"),
        [ContractDocumentKind.TemplatePackageManifestV5] = LoadSchema("template-package-v5.schema.json"),
        [ContractDocumentKind.TemplatePackageManifestV6] = LoadSchema("template-package-v6.schema.json"),
        [ContractDocumentKind.TemplateRuleWorkspace] = LoadSchema("template-rules-v1.schema.json"),
        [ContractDocumentKind.TemplateRuleAiResult] = LoadSchema("template-rule-ai-v1.schema.json"),
        [ContractDocumentKind.AssetManifest] = LoadSchema("asset-manifest-v1.schema.json"),
        [ContractDocumentKind.AiAnalysis] = LoadSchema("ai-analysis-v1.schema.json"),
        [ContractDocumentKind.ReferenceLearning] = LoadSchema("reference-learning-v1.schema.json"),
        [ContractDocumentKind.ReferenceLearningAi] = LoadSchema("reference-learning-ai-v2.schema.json")
    };

    private static ContractValidationDiagnosticResult DiagnosticFailure(
        string code,
        string userAction,
        string? path,
        string? keyword) =>
        new(ContractValidationResult.Failure(new ContractError(
            code,
            Retryable: false,
            Scope: "contract",
            UserAction: userAction)), path, keyword);
}
