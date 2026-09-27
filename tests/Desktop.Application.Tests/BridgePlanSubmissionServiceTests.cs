using System.Text.Json;
using Desktop.Application.Rendering;
using Desktop.Contracts.Bridge;
using Desktop.Contracts.Validation;

namespace Desktop.Application.Tests;

public sealed class BridgePlanSubmissionServiceTests
{
    [Fact]
    public async Task InvalidPlanNeverReachesExecutionPort()
    {
        var port = new RecordingPort();
        var request = BridgePlanSubmissionValidatorTests.Request();
        request["body"]!["payload"]!["render_plan"]!["output"]!["jpg"]!["file_name"] = @"C:\private\result.jpg";
        var result = await Create(port).SubmitAsync(request.ToJsonString(), CancellationToken.None);
        Assert.Equal("BRIDGE_PLAN_INVALID", result.Error!.Code);
        Assert.Equal(0, port.Calls);
    }

    [Fact]
    public async Task AcceptedResponseIsReturnedWithoutClaimingRenderCompletion()
    {
        var port = new RecordingPort { Result = Reply() };
        var request = BridgePlanSubmissionValidatorTests.Request().ToJsonString();
        var result = await Create(port).SubmitAsync(request, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal("accepted", result.Value!.ExecutionStatus);
        Assert.Equal(request, port.LastRequest);
        Assert.Equal(1, port.Calls);
    }

    [Theory]
    [InlineData("uncertain")]
    [InlineData("malformed")]
    [InlineData("missing_response")]
    [InlineData("wrong_request")]
    [InlineData("wrong_correlation")]
    [InlineData("late_cancellation")]
    [InlineData("invalid_delivery_state")]
    [InlineData("unsupported_date_encoding")]
    public async Task PossiblyDeliveredSubmissionBecomesUnknownWithoutRetry(string scenario)
    {
        var reply = Reply();
        var port = new RecordingPort
        {
            Result = scenario switch
            {
                "uncertain" => new(BridgePlanDelivery.Uncertain),
                "malformed" => new(BridgePlanDelivery.ResponseReceived, "private raw exception"),
                "missing_response" => new(BridgePlanDelivery.ResponseReceived),
                "wrong_request" => Reply(requestId: "request_other"),
                "wrong_correlation" => Reply(correlationId: "corr_other"),
                "invalid_delivery_state" => new((BridgePlanDelivery)999),
                "unsupported_date_encoding" => new(BridgePlanDelivery.ResponseReceived,
                    reply.ResponseJson!.Replace("1970-01-01T00:00:00+00:00", "1970-01-01t00:00:00z", StringComparison.Ordinal)),
                _ => reply
            },
            CancelDuringSubmission = scenario == "late_cancellation"
        };
        var result = await Create(port).SubmitAsync(
            BridgePlanSubmissionValidatorTests.Request().ToJsonString(), CancellationToken.None);
        Assert.Equal("BRIDGE_EXECUTION_UNKNOWN", result.Error!.Code);
        Assert.False(result.Error.Retryable);
        Assert.Null(result.Value);
        Assert.Equal(1, port.Calls);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedPlanReturnsOwnedErrorAndNeverRetries()
    {
        var port = new RecordingPort { Result = Reply(error: BridgeWireErrors.Create("BRIDGE_BUSY")) };
        var result = await Create(port).SubmitAsync(
            BridgePlanSubmissionValidatorTests.Request().ToJsonString(), CancellationToken.None);
        Assert.Equal("BRIDGE_BUSY", result.Error!.Code);
        Assert.Null(result.Value);
        Assert.Equal(1, port.Calls);
    }

    [Fact]
    public async Task DefinitelyUnsentPlanReturnsDisconnectedWithoutSubmittingAgain()
    {
        var port = new RecordingPort { Result = new(BridgePlanDelivery.NotSent) };
        var result = await Create(port).SubmitAsync(
            BridgePlanSubmissionValidatorTests.Request().ToJsonString(), CancellationToken.None);
        Assert.Equal("BRIDGE_DISCONNECTED", result.Error!.Code);
        Assert.Equal(1, port.Calls);
    }

    [Fact]
    public async Task CancellationBeforeSubmissionDoesNotCallPort()
    {
        var port = new RecordingPort();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Create(port).SubmitAsync(
            BridgePlanSubmissionValidatorTests.Request().ToJsonString(), cancellation.Token));
        Assert.Equal(0, port.Calls);
    }

    private static BridgePlanSubmissionService Create(RecordingPort port)
    {
        var wire = new BridgeWireValidator(new JsonSchemaContractValidator());
        return new(new(wire, new RenderPlanValidator(new JsonSchemaContractValidator())), wire, port);
    }

    private static BridgePlanDeliveryResult Reply(
        string requestId = "request_01", string correlationId = "corr_01JEXAMPLE", BridgeWireErrorV1? error = null)
    {
        var response = new BridgeResponseBodyV1(error is null, requestId, correlationId,
            error is null ? "accepted" : "rejected", DateTimeOffset.UnixEpoch,
            error is null ? "execution_01" : null, error is null ? "accepted" : null,
            error is null ? 1 : null, Error: error);
        return new(BridgePlanDelivery.ResponseReceived, JsonSerializer.Serialize(
            new BridgeWireEnvelope<BridgeResponseBodyV1>(BridgeProtocol.Version, "response", response), BridgeProtocol.JsonOptions));
    }

    private sealed class RecordingPort : IBridgePlanSubmissionPort
    {
        public BridgePlanDeliveryResult Result { get; init; } = new(BridgePlanDelivery.Uncertain);
        public bool CancelDuringSubmission { get; init; }
        public int Calls { get; private set; }
        public string? LastRequest { get; private set; }

        public Task<BridgePlanDeliveryResult> SubmitAsync(string requestJson, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = requestJson;
            return CancelDuringSubmission
                ? Task.FromException<BridgePlanDeliveryResult>(new OperationCanceledException())
                : Task.FromResult(Result);
        }
    }
}
