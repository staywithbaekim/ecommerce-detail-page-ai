using System.Text.Json;

namespace Desktop.Contracts.Bridge;

/// <summary>Explicit transport supplement; never add these fields to a ps-bridge/v1 envelope.</summary>
public static class BridgeAdapterProtocol
{
    public const string Version = "ps-adapter/v1";
    public const string Capability = "adapter/pull-v1";
    public const string PluginId = "local.ecommerce.detail-renderer";
    public const string PluginVersion = "0.1.1";
    public const int HeaderByteLimit = 8192;
    public const int ResourceChunkByteLimit = 128 * 1024;
}

public sealed record AdapterEnvelope<T>(string TransportVersion, string Kind, T Body);

public sealed record AdapterChallengeRequest(
    string RequestId, string ClientNonce, string PluginInstanceId, string PluginId,
    string PluginVersion, string PhotoshopBuild, string UxpRuntimeVersion,
    IReadOnlyList<string> Capabilities)
{
    public override string ToString() => nameof(AdapterChallengeRequest);
}

public sealed record AdapterChallenge(
    string RequestId, string ClientNonce, string Nonce, string SessionId,
    string PluginInstanceId, string TaskBinding, string AppVersion, string PluginId,
    string PluginVersion, string PhotoshopBuild, string UxpRuntimeVersion,
    long IssuedAtUnixSeconds, long ExpiresAtUnixSeconds, IReadOnlyList<string> Capabilities,
    string ServerProof)
{
    public override string ToString() => nameof(AdapterChallenge);
}

public sealed record AdapterClaimRequest;
public sealed record AdapterClaim(
    string ClaimId, string ExecutionId, string PlanDigest, string State,
    bool CancelRequested, JsonElement? Plan = null)
{
    public override string ToString() => nameof(AdapterClaim);
}
public sealed record AdapterClaimResponse(bool HasWork, AdapterClaim? Claim = null);
public sealed record AdapterClaimAcknowledgement(string ClaimId, string ExecutionId, string PlanDigest);
public sealed record AdapterEventReport(string ClaimId, JsonElement Event);
public sealed record AdapterEventReceipt(string ExecutionId, int EventSeq);
public sealed record AdapterResourceRead(
    string ClaimId, string ExecutionId, string PlanDigest, string SessionHandle, long Offset, int Count)
{
    public override string ToString() => nameof(AdapterResourceRead);
}
public sealed record AdapterResourceChunk(
    string ExecutionId, string PlanDigest, string SessionHandle, long Offset, int Count, string Sha256, string Data)
{
    public override string ToString() => nameof(AdapterResourceChunk);
}
public sealed record AdapterOutputWrite(string ClaimId, string ExecutionId, string PlanDigest, string OutputRootHandle,
    string ArtifactSetId, string OutputKind, long ByteLength, long Offset, int Count, string Sha256, string Data)
{
    public override string ToString() => nameof(AdapterOutputWrite);
}
public sealed record AdapterOutputSeal(string ClaimId, string ExecutionId, string PlanDigest, string OutputRootHandle,
    string ArtifactSetId, string OutputKind, long ByteLength, string Sha256)
{
    public override string ToString() => nameof(AdapterOutputSeal);
}
public sealed record AdapterOutputReceipt(string ExecutionId, string ArtifactSetId, string OutputKind, long NextOffset, bool Sealed);
public sealed record AdapterQueryMetadata(string ProtocolVersion, string CorrelationId, string SentAt, string IdempotencyKey);

/// <summary>HTTP authentication metadata; ToString never expands credentials.</summary>
public sealed record AdapterRequestAuthentication(
    string RequestId, string SessionId, string PluginInstanceId,
    long Sequence, string Token, string Mac, AdapterQueryMetadata? Query = null)
{
    public override string ToString() => nameof(AdapterRequestAuthentication);
}
