using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Application.Ai;
using Desktop.Application.Diagnostics;
using Desktop.Application.Templates;
using Desktop.Contracts.Ai;
using Desktop.Contracts.Validation;

namespace Desktop.Infrastructure.Ai;

public sealed partial class VolcArkAiAdapter
{
    private AiAnalysisExecutionResult ParseProviderResponse(AiAnalysisRequestV1 request, byte[] body, Stopwatch stopwatch)
        => new VolcArkAiResponseParser(_contractValidator, _options.ResponseShapeDiagnosticSink).Parse(request, body, stopwatch);

    private static AiProviderUsage? ParseProviderUsage(JsonElement root)
        => VolcArkAiResponseParser.ParseProviderUsage(root);

    private static AiProviderUsage? ParseResultUsage(JsonObject resultObject)
        => VolcArkAiResponseParser.ParseResultUsage(resultObject);

    private static string? ExtractOutputText(JsonElement root)
        => VolcArkAiResponseParser.ExtractOutputText(root);

    private static AiAnalysisExecutionResult MapHttpFailure(HttpStatusCode statusCode, Stopwatch stopwatch)
        => VolcArkAiResponseParser.MapHttpFailure(statusCode, stopwatch);

    private static AiAnalysisExecutionResult Failure(AiProviderOutcome outcome, string code, bool retryable, string userAction, Stopwatch stopwatch, AiProviderUsage? usage = null)
        => VolcArkAiResponseParser.Failure(outcome, code, retryable, userAction, stopwatch, usage);
}
