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

    private byte[] BuildProviderPayload(AiAnalysisRequestV1 request, List<AiAnalysisCopy> images)
        => VolcArkAiPayloadBuilder.BuildProviderPayload(request, images, _options.ModelId, JsonOptions);

    private static JsonObject IdSchema() => VolcArkAiPayloadBuilder.IdSchema();

    private static JsonObject StringSchema(int minLength, int maxLength)
        => VolcArkAiPayloadBuilder.StringSchema(minLength, maxLength);

    private static JsonObject ArraySchema(JsonObject itemSchema, int maxItems)
        => VolcArkAiPayloadBuilder.ArraySchema(itemSchema, maxItems);

    private static JsonObject UnitIntervalSchema()
        => new() { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 };

    private static JsonObject WarningSchema() => VolcArkAiPayloadBuilder.WarningSchema();

    private static JsonObject UsageSchema() => VolcArkAiPayloadBuilder.UsageSchema();
}
