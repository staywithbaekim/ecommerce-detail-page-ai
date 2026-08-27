using Desktop.Application.Rendering;
using Desktop.Contracts.Ai;
using Desktop.Contracts.Bridge;
using Desktop.Contracts.Validation;
using Desktop.Infrastructure.Ai;
using Desktop.Infrastructure.Bridge;

namespace Desktop.Infrastructure.Tests;

public sealed class MockAdapterTests
{
    [Fact]
    public async Task BridgeCoversHandshakeIdempotencyAndUnknownExecution()
    {
        var bridge = CreateBridge();
        var session = await HandshakeAsync(bridge);
        var request = new BridgeExecutionRequest(
            "request-0001",
            "idempotency-0001",
            "corr-001",
            LoadValidRenderPlan(),
            session.AccessToken);

        var duplicates = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => bridge.SubmitAsync(request, CancellationToken.None)));
        var unknown = await bridge.QueryAsync(
            "execution-unknown",
            session.AccessToken,
            CancellationToken.None);

        Assert.All(duplicates, result => Assert.True(result.IsSuccess));
        Assert.Single(duplicates.Select(result => result.Value!.ExecutionId).Distinct(StringComparer.Ordinal));
        Assert.Equal("BRIDGE_EXECUTION_NOT_FOUND", unknown.Error!.Code);
        Assert.False(unknown.Error.Retryable);
        Assert.NotEmpty(unknown.Error.UserAction);
    }

    [Fact]
    public async Task BridgeRejectsHandshakeExpiredTokenAndOversizedPlan()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero));
        var bridge = CreateBridge(clock);
        var rejected = await bridge.HandshakeAsync(
            new BridgeHandshakeRequest("ps-bridge/v1", "client-01", new string('A', 43), "reject"),
            CancellationToken.None);
        var session = await HandshakeAsync(bridge);
        var oversized = await bridge.SubmitAsync(
            new BridgeExecutionRequest(
                "request-0002",
                "idempotency-0002",
                "corr-002",
                new string('x', MockBridgeClient.MaxPlanBytes + 1),
                session.AccessToken),
            CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(16));
        var expired = await bridge.QueryAsync(
            "execution-unknown",
            session.AccessToken,
            CancellationToken.None);

        Assert.Equal("BRIDGE_HANDSHAKE_REJECTED", rejected.Error!.Code);
        Assert.Equal("BRIDGE_REQUEST_TOO_LARGE", oversized.Error!.Code);
        Assert.Equal("BRIDGE_TOKEN_INVALID", expired.Error!.Code);
    }

    [Fact]
    public async Task AiMockUsesGenericSemanticsAndNoCredential()
    {
        var credentialSource = new NullAiCredentialSource();
        var analyzer = new MockAiAnalyzer();
        var result = await analyzer.AnalyzeAsync(
            new AiAnalysisRequest(
                "request-01",
                "product-01",
                ["asset-01", "asset-02"],
                ["primary-view", "material-detail"]),
            CancellationToken.None);

        Assert.Null(await credentialSource.GetCredentialAsync(CancellationToken.None));
        Assert.Equal(["primary-view", "material-detail"], result.Candidates.Select(item => item.SemanticLabel));
    }

    private static MockBridgeClient CreateBridge(TimeProvider? timeProvider = null) => new(
        new RenderPlanValidator(new JsonSchemaContractValidator()),
        timeProvider);

    private static async Task<BridgeSession> HandshakeAsync(MockBridgeClient bridge)
    {
        var result = await bridge.HandshakeAsync(
            new BridgeHandshakeRequest("ps-bridge/v1", "client-01", new string('A', 43)),
            CancellationToken.None);
        return Assert.IsType<BridgeSession>(result.Value);
    }

    private static string LoadValidRenderPlan() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "render-plan-v1.valid.example.json"));

    private sealed class ManualTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset _current = current;

        public override DateTimeOffset GetUtcNow() => _current;

        public void Advance(TimeSpan value) => _current = _current.Add(value);
    }
}
