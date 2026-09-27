using System.Text;
using System.Text.Json;
using Desktop.Contracts.Bridge;
using Desktop.Contracts.Validation;
using Desktop.Infrastructure.Bridge;

namespace Desktop.Infrastructure.Tests;

public sealed class BridgeWireMessageReaderTests
{
    private readonly BridgeWireMessageReader _reader = new(new(new JsonSchemaContractValidator()));

    [Fact]
    public async Task ReadsFragmentedUtf8WithoutLoggingSecretsOrOwningStream()
    {
        var body = new BridgeRequestBodyV1<BridgeCancelPayloadV1>("request_01", "corr_01", "session_01",
            new string('T', 43), DateTimeOffset.UnixEpoch, "cancel_01", "cancel_execution",
            new("用户暂停"), "execution_01");
        var json = Serialize(body).Replace("\\u7528\\u6237\\u6682\\u505C", "用户暂停", StringComparison.Ordinal);
        Assert.Contains("用户暂停", json, StringComparison.Ordinal);
        using var stream = new FragmentedStream(Encoding.UTF8.GetBytes(json), chunkSize: 1);
        var result = await _reader.ReadAsync(stream, BridgeEndpoint.Cancel, false, null, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(json, result.Json);
        Assert.DoesNotContain(new string('T', 43), result.ToString(), StringComparison.Ordinal);
        Assert.False(stream.Disposed);
    }

    [Theory]
    [InlineData(BridgeEndpoint.Handshake, false)]
    [InlineData(BridgeEndpoint.Plans, false)]
    [InlineData(BridgeEndpoint.Plans, true)]
    [InlineData(BridgeEndpoint.Execution, true)]
    [InlineData(BridgeEndpoint.Heartbeat, false)]
    public async Task DeclaredOversizeIsRejectedWithoutReading(BridgeEndpoint endpoint, bool response)
    {
        using var stream = new FragmentedStream([]);
        var result = await _reader.ReadAsync(stream, endpoint, response,
            (long)BridgeWireValidator.ByteLimit(endpoint, response) + 1, CancellationToken.None);
        Assert.Equal("BRIDGE_REQUEST_TOO_LARGE", result.Error!.Code);
        Assert.Null(result.Json);
        Assert.Equal(0, stream.ReadCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1L)]
    public async Task UnknownOrMisreportedLengthCannotReadBeyondLimitPlusOne(long? declaredLength)
    {
        var limit = BridgeProtocol.StatusByteLimit;
        using var stream = new FragmentedStream(new byte[limit * 2]);
        var result = await _reader.ReadAsync(stream, BridgeEndpoint.Heartbeat, false, declaredLength, CancellationToken.None);
        Assert.Equal("BRIDGE_REQUEST_TOO_LARGE", result.Error!.Code);
        Assert.Null(result.Json);
        Assert.Equal(limit + 1, stream.BytesRead);
    }

    [Fact]
    public async Task AcceptsExactLimitButRejectsEvenOneExtraByte()
    {
        var json = Serialize(new BridgeRequestBodyV1<BridgeEmptyPayloadV1>("request_01", "corr_01", "session_01",
            new string('T', 43), DateTimeOffset.UnixEpoch, "release_01", "release", new()));
        var padded = Encoding.UTF8.GetBytes(json.PadRight(BridgeProtocol.MessageByteLimit));
        using var exact = new FragmentedStream(padded);
        var result = await _reader.ReadAsync(exact, BridgeEndpoint.Release, false, padded.Length, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(padded.Length, Encoding.UTF8.GetByteCount(result.Json!));
        using var oversized = new FragmentedStream([.. padded, (byte)' ']);
        result = await _reader.ReadAsync(oversized, BridgeEndpoint.Release, false, null, CancellationToken.None);
        Assert.Equal("BRIDGE_REQUEST_TOO_LARGE", result.Error!.Code);
        Assert.Null(result.Json);
    }

    [Theory]
    [InlineData("invalid_utf8")]
    [InlineData("truncated_json")]
    [InlineData("truncated_body")]
    [InlineData("extra_body")]
    [InlineData("negative_length")]
    [InlineData("invalid_schema")]
    public async Task RejectsInvalidBodiesWithoutReturningPartialJson(string scenario)
    {
        var json = Serialize(new BridgeRequestBodyV1<BridgeEmptyPayloadV1>("request_01", "corr_01", "session_01",
            new string('T', 43), DateTimeOffset.UnixEpoch, "release_01", "release", new()));
        var bytes = scenario switch
        {
            "truncated_json" => Encoding.UTF8.GetBytes(json[..^1]),
            "invalid_schema" => Encoding.UTF8.GetBytes("{}"),
            _ => Encoding.UTF8.GetBytes(json)
        };
        if (scenario == "invalid_utf8")
        {
            // Put an illegal leading byte inside an otherwise valid string.
            bytes[Encoding.UTF8.GetByteCount(json[..json.IndexOf("corr_01", StringComparison.Ordinal)])] = 0xFF;
        }
        long? length = scenario switch
        {
            "truncated_body" => bytes.Length + 1,
            "extra_body" => bytes.Length - 1,
            "negative_length" => -1,
            _ => null
        };
        using var stream = new FragmentedStream(bytes, chunkSize: 7);
        var result = await _reader.ReadAsync(stream, BridgeEndpoint.Release, false, length, CancellationToken.None);
        Assert.Equal("BRIDGE_PROTOCOL_MISMATCH", result.Error!.Code);
        Assert.Null(result.Json);
    }

    [Fact]
    public async Task BrokenStreamIsSanitizedAndNeverTreatedAsPartialSuccess()
    {
        using var stream = new FragmentedStream(Encoding.UTF8.GetBytes("{\"body\":"), failAfterData: true);
        var result = await _reader.ReadAsync(stream, BridgeEndpoint.Release, false, null, CancellationToken.None);
        Assert.Equal("BRIDGE_DISCONNECTED", result.Error!.Code);
        Assert.Null(result.Json);
        Assert.DoesNotContain("private", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.False(stream.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationPropagatesToOwnerWithoutPartialSuccess(bool duringRead)
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new FragmentedStream([1, 2, 3], cancelOnRead: duringRead ? cancellation : null);
        if (!duringRead) cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _reader.ReadAsync(
            stream, BridgeEndpoint.Release, false, null, cancellation.Token));
        Assert.Equal(duringRead ? 1 : 0, stream.ReadCalls);
        Assert.False(stream.Disposed);
    }

    private static string Serialize<T>(T body) => JsonSerializer.Serialize(
        new BridgeWireEnvelope<T>(BridgeProtocol.Version, "request", body), BridgeProtocol.JsonOptions);

    private sealed class FragmentedStream(byte[] data, int chunkSize = 4096, bool failAfterData = false,
        CancellationTokenSource? cancelOnRead = null) : Stream
    {
        public int BytesRead { get; private set; }
        public int ReadCalls { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            cancelOnRead?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            if (BytesRead == data.Length && failAfterData) throw new IOException(@"C:\private\source.psd");
            var count = Math.Min(Math.Min(buffer.Length, chunkSize), data.Length - BytesRead);
            data.AsMemory(BytesRead, count).CopyTo(buffer);
            BytesRead += count;
            return ValueTask.FromResult(count);
        }

        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
