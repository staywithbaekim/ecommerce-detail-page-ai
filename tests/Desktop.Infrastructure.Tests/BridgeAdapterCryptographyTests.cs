using Desktop.Contracts.Bridge;
using Desktop.Application.Rendering;
using Desktop.Infrastructure.Bridge;

namespace Desktop.Infrastructure.Tests;

public sealed class BridgeAdapterCryptographyTests
{
    [Fact]
    public void DotNetMatchesIndependentJavaScriptNobleVectors()
    {
        // Produced by uxp-render-adapter/tests/auth-vector.cjs using a public, non-production key.
        var secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var challenge = new AdapterChallenge("request_vector", Token(1), Token(2), "session_vector", "instance_vector",
            Token(3), "0.1.0", BridgeAdapterProtocol.PluginId, "0.1.0", "26.8.1 test-build", "8.1",
            1800316800, 1800316830, ["output/jpeg-srgb-icc", BridgeAdapterProtocol.Capability], "");
        var proof = BridgeAdapterCryptography.ChallengeProof(secret, challenge);
        Assert.Equal("MfxFJkmmwRyI4x7aVKJ6c8rIH59QAOhzM3tZ8P1k0sY", proof);
        challenge = challenge with { ServerProof = proof };
        var key = BridgeAdapterCryptography.SessionKey(secret, challenge);
        Assert.Equal("4JuTkP-MORI0UZUkCydW-y1DEqDor6n5DRE9iKesNN8", BridgeAdapterCryptography.Encode(key));
        var auth = new AdapterRequestAuthentication("request_mac", challenge.SessionId, challenge.PluginInstanceId, 7, Token(4), "");
        Assert.Equal("hxVW7L-R8nysHAhFDdqaNvzU3Q01ApUIu9f5GoIIHdA",
            BridgeAdapterCryptography.RequestMac(key, auth, "POST", "/adapter/v1/claims", "{}"));
        var query = auth with { Query = new(BridgeProtocol.Version, "corr_query", "2026-09-19T00:00:00+00:00", "query_execution_1") };
        var queryMac = BridgeAdapterCryptography.RequestMac(key, query, "GET", "/executions/execution_1?after_seq=2", "");
        Assert.Equal("dEtdXxMTq79Jwsw3UH_QO3N8MCiyuqZmbKtMqpNzd2U", queryMac);
        Assert.NotEqual(queryMac, BridgeAdapterCryptography.RequestMac(key,
            query with { Query = query.Query! with { CorrelationId = "changed" } }, "GET", "/executions/execution_1?after_seq=2", ""));
        Assert.Equal("QWw46OHCj5QXdIZV6onNJsVQjMBbxkRrBNyIkRMJafE",
            BridgeAdapterCryptography.ResponseMac(key, auth, 200, "{\"值\":1}"));
        Assert.Equal("FxkzCHJxFqAOeKeh7kYGGXnISGdd4Ba-vA7bhy2p4M4",
            BridgeAdapterCryptography.Sign(secret, "ps-adapter/v1", "中文", "", "a\nb"));
    }

    [Theory]
    [InlineData(false, "t0VnCAz-UQvOeUuhmgE18b3I6BTLWeiuC7yn2dndrJA", "B5f2bQo9UodqBLGqxh2at7GasXvp8h0fUssYu_fdsKU",
        "zm_r9_xfhdad_lw-Jgw7zl77S9iaUJXQc7Z8sgnZcsk", "EuvXC41no4h2otIo81iZSuXeBr_ezIp9qMB7Vrrom9c")]
    [InlineData(true, "Tu62e3oqcsTPgIfr9BYNC99fD7mQBhR5eTmf-BBauCw", "WpduFqE-U836zlwIhn7PA7qa4yksaD3TR5NYwUzmHAk",
        "THmEzq6z8wWNymTIdL4HCo4wvLmasnNQPMM_orSRlKM", "_40iHXlCgk_PkYAotfopB9X54KFFFq23mcZ8R-Ag5ug")]
    public void ApprovedFullBuildAndInstanceTranscriptsMatchJavaScriptPublicVectors(bool instanceContract,
        string expectedProof, string expectedKey, string expectedContext, string expectedResponse)
    {
        // Same public 00..1f key and fixed nonce values as auth-vector.cjs. No protected-store access.
        var secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        string[] capabilities = [BridgeAdapterProtocol.Capability, "psd-layer-path/psd-bottom-to-top",
            "smart-object/replace", "output/jpeg-srgb-icc", RenderPlanPlacementMigration.Capability,
            RenderOutputEvidenceValidator.VerificationPsdCapability, RenderPlanPlacementMigration.SizeTableCapability,
            RenderPlanPlacementMigration.SizeInsertionCapability, RenderPlanVariantIdentity.Capability];
        if (instanceContract) capabilities = [.. capabilities, RenderPlanInstanceIdentity.Capability];
        var challenge = new AdapterChallenge("request_vector", Token(1), Token(2), "session_vector", "instance_vector",
            Token(3), "0.1.0", BridgeAdapterProtocol.PluginId, BridgeAdapterProtocol.PluginVersion,
            BridgeSessionOwnerFactory.SupportedPhotoshopBuild, "uxp-8.3.0-uxp-b1d8c85", 1800316800, 1800316830, capabilities, "");
        Assert.Equal(expectedProof, BridgeAdapterCryptography.ChallengeProof(secret, challenge));
        Assert.Equal(expectedProof, BridgeAdapterCryptography.ChallengeProof(secret,
            challenge with { Capabilities = capabilities.Reverse().ToArray() }));
        challenge = challenge with { ServerProof = expectedProof };
        var key = BridgeAdapterCryptography.SessionKey(secret, challenge);
        Assert.Equal(expectedKey, BridgeAdapterCryptography.Encode(key));
        Assert.Equal(expectedContext, BridgeAdapterCryptography.HandshakeContextProof(secret, challenge, "{\"值\":1}"));
        Assert.Equal(expectedResponse, BridgeAdapterCryptography.HandshakeResponseProof(key, challenge.RequestId, "{\"值\":1}"));
        Assert.NotEqual(expectedProof, BridgeAdapterCryptography.ChallengeProof(secret, challenge with { PhotoshopBuild = "other" }));
        Assert.False(BridgeAdapterCryptography.FixedEquals(expectedProof, Token(9)));
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
    }

    private static string Token(byte value) => BridgeAdapterCryptography.Encode(Enumerable.Repeat(value, 32).ToArray());
}
