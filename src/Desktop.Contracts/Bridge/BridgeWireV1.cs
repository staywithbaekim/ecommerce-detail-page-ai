using System.Text.Json;
using System.Text.Json.Serialization;

namespace Desktop.Contracts.Bridge;

/// <summary>Wire DTOs for the frozen Schema; the foundation mock DTOs are not wire messages.</summary>
public static class BridgeProtocol
{
    public const string Version = "ps-bridge/v1";
    public const int RendezvousPort = 17842;
    public const int MinExecutionPort = 49152;
    public const int MaxExecutionPort = 65535;
    public const int MessageByteLimit = 1024 * 1024;
    public const int PlanByteLimit = 8 * 1024 * 1024;
    public const int StatusByteLimit = 256 * 1024;

    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            PropertyNameCaseInsensitive = false,
            MaxDepth = 64
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

public sealed record BridgeWireEnvelope<T>(string ProtocolVersion, string EnvelopeKind, T Body);

public sealed record BridgeClientInfoV1(
    string AppVersion, string ProtocolVersion, IReadOnlyList<string>? RequestedCapabilities = null);

public sealed record BridgeHostInfoV1(
    string PluginManifestVersion, string PhotoshopBuild, string? UxpRuntimeVersion = null);

public sealed record BridgeHandshakeBodyV1(
    string RequestId, string CorrelationId, DateTimeOffset SentAt, string SessionId,
    string Nonce, BridgeClientInfoV1 Client, BridgeHostInfoV1 Host, string PairingProof)
{
    public override string ToString() => nameof(BridgeHandshakeBodyV1);
}

public sealed record BridgeHandshakeResultV1(
    bool Ok, string RequestId, string CorrelationId, string Status, DateTimeOffset ServerTime,
    string? SessionId = null, int? ExecutionPort = null, string? Token = null,
    DateTimeOffset? ExpiresAt = null, IReadOnlyList<string>? Capabilities = null,
    BridgeWireErrorV1? Error = null)
{
    public override string ToString() => nameof(BridgeHandshakeResultV1);
}

public sealed record BridgeRequestBodyV1<T>(
    string RequestId, string CorrelationId, string SessionId, string Token, DateTimeOffset SentAt,
    string IdempotencyKey, string Operation, T Payload, string? ExecutionId = null)
{
    public override string ToString() => nameof(BridgeRequestBodyV1<T>);
}

public sealed record BridgePlanPayloadV1(
    string ExecutionScope, JsonElement RenderPlan, string PlanDigest,
    IReadOnlyList<BridgeResourceBindingV1> ResourceBindings,
    IReadOnlyList<BridgeOutputBindingV1> OutputBindings);

public sealed record BridgeResourceBindingV1(
    string ResourceId, string SessionHandle, string Sha256, string Purpose, bool ReadOnly,
    long? ByteLength = null)
{
    public override string ToString() => nameof(BridgeResourceBindingV1);
}

public sealed record BridgeOutputBindingV1(
    string ArtifactSetId, string OutputRootHandle, string OverwritePolicy,
    IReadOnlyList<string> ConfirmedTargetIds)
{
    public override string ToString() => nameof(BridgeOutputBindingV1);
}

public sealed record BridgeHeartbeatPayloadV1(string HostState, string ModalState, int LastEventSeq);
public sealed record BridgeCancelPayloadV1(string Reason);
public sealed record BridgeEmptyPayloadV1;

public sealed record BridgeResponseBodyV1(
    bool Ok, string RequestId, string CorrelationId, string Status, DateTimeOffset ServerTime,
    string? ExecutionId = null, string? ExecutionStatus = null, int? EventSeq = null,
    IReadOnlyList<BridgeEventV1>? Events = null, BridgeWireErrorV1? Error = null);

public sealed record BridgeEventV1(
    string EventId, int EventSeq, DateTimeOffset OccurredAt, string EventType,
    string ExecutionId, string Status, string? PageId = null, BridgeWireErrorV1? Error = null);

public sealed record BridgeWireErrorV1(
    string Code, bool Retryable, string Scope, string UserAction,
    IReadOnlyDictionary<string, string>? Details = null);
