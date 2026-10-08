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
    private sealed record ProviderTransportFailure(
        AiProviderOutcome Outcome,
        string Code,
        bool Retryable,
        string UserAction);

    /// <summary>
    /// Writes the already-bounded payload while recording the exact point at
    /// which HttpClient finished serializing it to the transport stream. This
    /// does not prove that the remote service accepted the bytes, but it lets
    /// diagnostics distinguish local submission/upload stalls from waiting for
    /// response headers.
    /// </summary>
    private sealed class ObservableByteArrayContent(
        byte[] payload,
        Action<int> uploadProgress,
        Action uploadCompleted) : HttpContent
    {
        // Report progress at a granularity that remains observable on slow
        // uplinks.  The previous 1 MiB write meant one blocked WriteAsync
        // could exceed the 45 s idle guard before a single progress callback,
        // even though the connection was still capable of making progress.
        private const int UploadChunkBytes = 64 * 1024;
        private int _completionReported;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeCoreAsync(stream, CancellationToken.None);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken) =>
            SerializeCoreAsync(stream, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = payload.LongLength;
            return true;
        }

        private async Task SerializeCoreAsync(Stream stream, CancellationToken cancellationToken)
        {
            var written = 0;
            while (written < payload.Length)
            {
                var count = Math.Min(UploadChunkBytes, payload.Length - written);
                await stream.WriteAsync(payload.AsMemory(written, count), cancellationToken)
                    .ConfigureAwait(false);
                written += count;
                uploadProgress(written);
            }

            if (Interlocked.Exchange(ref _completionReported, 1) == 0)
            {
                uploadCompleted();
            }
        }
    }

    /// <summary>
    /// Emits phase-only transport evidence to the application-owned redacted
    /// JSONL log. It never records credentials, payload content, response
    /// content, media names, paths or exception messages. The only provider
    /// value retained is a bounded opaque request/correlation id for support.
    /// </summary>
    private sealed class AiTransportTrace(
        IRedactedLogger? logger,
        string requestId,
        string operation,
        Stopwatch stopwatch,
        int payloadBytes,
        int assetCount,
        int requirementCount,
        VolcArkAiOptions options)
    {
        private int _uploadCompleted;
        private int _uploadBytesWritten;
        private long _uploadStartedTimestamp;
        private long _lastUploadProgressTimestamp;
        private int _sendStarted;
        private int _responseHeadersReceived;
        private int _responseBodyCompleted;
        private int _failureLogged;
        private int? _httpStatusCode;
        private int? _responseBytes;
        private string? _providerRequestReference;

        private bool UploadCompleted => Volatile.Read(ref _uploadCompleted) != 0;

        private bool ResponseHeadersReceived => Volatile.Read(ref _responseHeadersReceived) != 0;

        public bool IsUploadCompleted => UploadCompleted;

        public TimeSpan UploadElapsed => ElapsedSince(_uploadStartedTimestamp, stopwatch.Elapsed);

        public TimeSpan UploadIdleElapsed => ElapsedSince(
            Volatile.Read(ref _lastUploadProgressTimestamp),
            UploadElapsed);

        public void MarkPayloadReady() => Information("payload_ready");

        public void MarkSendStarted()
        {
            if (Interlocked.Exchange(ref _sendStarted, 1) == 0)
            {
                var timestamp = Stopwatch.GetTimestamp();
                Interlocked.Exchange(ref _uploadStartedTimestamp, timestamp);
                Interlocked.Exchange(ref _lastUploadProgressTimestamp, timestamp);
            }

            Information("send_started");
        }

        public void MarkUploadProgress(int bytesWritten)
        {
            if (bytesWritten <= Volatile.Read(ref _uploadBytesWritten))
            {
                return;
            }

            Interlocked.Exchange(ref _uploadBytesWritten, bytesWritten);
            Interlocked.Exchange(ref _lastUploadProgressTimestamp, Stopwatch.GetTimestamp());
            Information("upload_progress");
        }

        public void MarkUploadCompleted()
        {
            Interlocked.Exchange(ref _lastUploadProgressTimestamp, Stopwatch.GetTimestamp());
            if (Interlocked.Exchange(ref _uploadCompleted, 1) == 0)
            {
                Information("upload_completed");
            }
        }

        public void MarkResponseHeaders(HttpResponseMessage response)
        {
            ArgumentNullException.ThrowIfNull(response);
            _httpStatusCode = (int)response.StatusCode;
            _providerRequestReference ??= ExtractProviderRequestReference(response);
            if (Interlocked.Exchange(ref _responseHeadersReceived, 1) == 0)
            {
                Information("response_headers_received");
            }
        }

        public void MarkResponseBodyStarted() => Information("response_body_started");

        public void MarkResponseBodyCompleted(byte[] body)
        {
            ArgumentNullException.ThrowIfNull(body);
            _responseBytes = body.Length;
            _providerRequestReference ??= ExtractProviderRequestReference(body);
            if (Interlocked.Exchange(ref _responseBodyCompleted, 1) == 0)
            {
                Information("response_body_completed");
            }
        }

        public void MarkLateResponseHeaders(HttpResponseMessage response)
        {
            ArgumentNullException.ThrowIfNull(response);
            _httpStatusCode = (int)response.StatusCode;
            _providerRequestReference ??= ExtractProviderRequestReference(response);
            Interlocked.Exchange(ref _responseHeadersReceived, 1);
            Information("late_response_headers_received", late: true);
        }

        public void MarkLateBodyCompleted(byte[] body)
        {
            ArgumentNullException.ThrowIfNull(body);
            _responseBytes = body.Length;
            _providerRequestReference ??= ExtractProviderRequestReference(body);
            Interlocked.Exchange(ref _responseBodyCompleted, 1);
            Information("late_response_body_completed", late: true);
        }

        public void MarkLateFailure(Exception exception) =>
            Error("AI_TRANSPORT_LATE_FAILURE", "late_transport_failure", exception, late: true);

        public ProviderTransportFailure CreateTimeoutFailure(Exception? exception = null)
        {
            ProviderTransportFailure failure;
            if (ResponseHeadersReceived)
            {
                failure = new(
                    AiProviderOutcome.AiResultUnknown,
                    "AI_PROVIDER_RESPONSE_BODY_TIMEOUT",
                    false,
                    "已收到供应商响应头，但读取响应体超时；处理结果未知，禁止自动重发。");
            }
            else if (UploadCompleted)
            {
                failure = new(
                    AiProviderOutcome.AiResultUnknown,
                    "AI_PROVIDER_RESPONSE_HEADERS_TIMEOUT",
                    false,
                    "AI 请求体已写入传输通道，但截止时间内未收到响应头；供应商是否接收仍未知，禁止自动重发。");
            }
            else
            {
                failure = new(
                    AiProviderOutcome.RetryableError,
                    "AI_PROVIDER_SUBMISSION_TIMEOUT",
                    true,
                    "AI 请求体未完整写入传输通道，未形成可被供应商处理的完整请求；请检查网络后重试。");
            }

            MarkFailure(failure.Code, exception);
            return failure;
        }

        public ProviderTransportFailure CreateNetworkFailure(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            ProviderTransportFailure failure;
            if (ResponseHeadersReceived)
            {
                failure = new(
                    AiProviderOutcome.AiResultUnknown,
                    "AI_PROVIDER_RESPONSE_BODY_NETWORK_FAILURE",
                    false,
                    "已收到供应商响应头，但读取响应体时网络中断；处理结果未知，禁止自动重发。");
            }
            else if (UploadCompleted)
            {
                failure = new(
                    AiProviderOutcome.AiResultUnknown,
                    "AI_PROVIDER_RESPONSE_HEADERS_NETWORK_FAILURE",
                    false,
                    "AI 请求体已写入传输通道，但收到响应头前网络中断；供应商是否接收仍未知，禁止自动重发。");
            }
            else
            {
                failure = new(
                    AiProviderOutcome.RetryableError,
                    "AI_PROVIDER_SUBMISSION_NETWORK_FAILURE",
                    true,
                    "AI 请求体尚未完整写入时网络中断，未形成可被供应商处理的完整请求；请检查网络后重试。");
            }

            MarkFailure(failure.Code, exception);
            return failure;
        }

        public void MarkFailure(string errorCode, Exception? exception = null)
        {
            if (Interlocked.Exchange(ref _failureLogged, 1) != 0)
            {
                return;
            }

            Error(errorCode, CurrentFailureStage(), exception, late: false);
        }

        private string CurrentFailureStage() => ResponseHeadersReceived
            ? "reading_response_body"
            : UploadCompleted
                ? "waiting_response_headers"
                : "submitting_request";

        private void Information(string phase, bool late = false)
        {
            if (logger is null)
            {
                return;
            }

            try
            {
                logger.Information("AI_TRANSPORT_PHASE", Properties(phase, late, null));
            }
            catch
            {
                // Diagnostic storage must never alter provider behavior.
            }
        }

        private void Error(string errorCode, string phase, Exception? exception, bool late)
        {
            if (logger is null)
            {
                return;
            }

            try
            {
                logger.LogError(
                    "AI_TRANSPORT_FAILURE",
                    errorCode,
                    Properties(phase, late, exception));
            }
            catch
            {
                // Diagnostic storage must never alter provider behavior.
            }
        }

        private Dictionary<string, object?> Properties(
            string phase,
            bool late,
            Exception? exception)
        {
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["request_id"] = requestId,
                ["operation"] = operation,
                ["phase"] = phase,
                ["elapsed_ms"] = (long)Math.Ceiling(stopwatch.Elapsed.TotalMilliseconds),
                ["payload_bytes"] = payloadBytes,
                ["asset_count"] = assetCount,
                ["requirement_count"] = requirementCount,
                ["timeout_ms"] = (long)Math.Ceiling(options.EffectiveRequestTimeout.TotalMilliseconds),
                ["upload_timeout_ms"] = (long)Math.Ceiling(options.EffectiveUploadTimeout.TotalMilliseconds),
                ["upload_idle_timeout_ms"] = (long)Math.Ceiling(options.EffectiveUploadIdleTimeout.TotalMilliseconds),
                ["response_headers_timeout_ms"] = (long)Math.Ceiling(options.EffectiveResponseHeadersTimeout.TotalMilliseconds),
                ["response_body_timeout_ms"] = (long)Math.Ceiling(options.EffectiveResponseBodyTimeout.TotalMilliseconds),
                ["upload_completed"] = UploadCompleted,
                ["upload_bytes_written"] = Volatile.Read(ref _uploadBytesWritten),
                ["upload_elapsed_ms"] = (long)Math.Ceiling(UploadElapsed.TotalMilliseconds),
                ["upload_idle_ms"] = (long)Math.Ceiling(UploadIdleElapsed.TotalMilliseconds),
                ["response_headers_received"] = ResponseHeadersReceived,
                ["response_body_completed"] = Volatile.Read(ref _responseBodyCompleted) != 0,
                ["http_status_code"] = _httpStatusCode,
                ["response_bytes"] = _responseBytes,
                ["provider_request_reference"] = _providerRequestReference,
                ["late"] = late,
                ["exception_type"] = exception?.GetType().Name
            };
            if (exception is HttpRequestException httpException)
            {
                properties["http_request_error"] = httpException.HttpRequestError.ToString();
                properties["exception_status_code"] = httpException.StatusCode is null
                    ? null
                    : (int)httpException.StatusCode.Value;
            }

            var socketException = FindSocketException(exception);
            properties["socket_error"] = socketException?.SocketErrorCode.ToString();
            return properties;
        }

        private static string? ExtractProviderRequestReference(HttpResponseMessage response)
        {
            foreach (var headerName in new[] { "x-request-id", "x-tt-logid", "x-trace-id", "trace-id" })
            {
                if (response.Headers.TryGetValues(headerName, out var values))
                {
                    var value = values.FirstOrDefault(IsSafeProviderReference);
                    if (value is not null)
                    {
                        return value;
                    }
                }
            }

            return null;
        }

        private static TimeSpan ElapsedSince(long startTimestamp, TimeSpan fallback)
        {
            if (startTimestamp <= 0)
            {
                return fallback;
            }

            var elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
            return elapsedTicks <= 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds((double)elapsedTicks / Stopwatch.Frequency);
        }

        private static string? ExtractProviderRequestReference(byte[] body)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String
                    && id.GetString() is { } value
                    && IsSafeProviderReference(value))
                {
                    return value;
                }
            }
            catch (JsonException)
            {
                // Response parsing remains the caller's responsibility. A
                // diagnostic correlation lookup never changes that outcome.
            }

            return null;
        }

        private static bool IsSafeProviderReference(string value) =>
            value.Length is > 0 and <= 192
            && value.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.' or ':');

        private static SocketException? FindSocketException(Exception? exception)
        {
            for (var current = exception; current is not null; current = current.InnerException)
            {
                if (current is SocketException socketException)
                {
                    return socketException;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Owns one provider request and gives the adapter a hard, caller-visible
    /// deadline even when a custom/native HttpMessageHandler ignores the
    /// cancellation token.  A timeout only stops waiting; the underlying task
    /// is still observed and its response/request are disposed later so that
    /// no background exception is lost and no live handler uses a disposed
    /// HttpRequestMessage.
    /// </summary>
    private sealed class HttpRequestScope : IDisposable
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

        private readonly HttpRequestMessage _message;
        private readonly CancellationToken _callerToken;
        private readonly CancellationTokenSource _transportCancellation;
        private readonly VolcArkAiOptions _options;
        private readonly Stopwatch _deadlineClock = Stopwatch.StartNew();
        private HttpResponseMessage? _response;
        private int _cleanupScheduled;
        private int _deadlineCancellationRequested;

        public HttpRequestScope(
            HttpRequestMessage message,
            VolcArkAiOptions options,
            CancellationToken callerToken)
        {
            _message = message ?? throw new ArgumentNullException(nameof(message));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _callerToken = callerToken;
            _transportCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        }

        public CancellationToken TransportToken => _transportCancellation.Token;

        public void AttachResponse(HttpResponseMessage response)
        {
            _response = response ?? throw new ArgumentNullException(nameof(response));
        }

        private TimeSpan TotalRemaining
        {
            get
            {
                var remaining = _options.EffectiveRequestTimeout - _deadlineClock.Elapsed;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        public Task<HttpResponseMessage> WaitForResponseHeadersAsync(
            Task<HttpResponseMessage> operation,
            AiTransportTrace trace) =>
            WaitForPhaseAsync(operation, trace, bodyPhase: false);

        public Task<byte[]> WaitForResponseBodyAsync(
            Task<byte[]> operation,
            AiTransportTrace trace) =>
            WaitForPhaseAsync(operation, trace, bodyPhase: true);

        private async Task<T> WaitForPhaseAsync<T>(
            Task<T> operation,
            AiTransportTrace trace,
            bool bodyPhase)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ArgumentNullException.ThrowIfNull(trace);

            var responseHeadersClock = Stopwatch.StartNew();
            var responseHeadersClockStarted = bodyPhase;
            while (true)
            {
                _callerToken.ThrowIfCancellationRequested();
                if (operation.IsCompleted)
                {
                    return await operation.ConfigureAwait(false);
                }

                var totalRemaining = TotalRemaining;
                if (totalRemaining <= TimeSpan.Zero)
                {
                    CancelForDeadline();
                    throw new TimeoutException("AI request total deadline elapsed.");
                }

                TimeSpan phaseRemaining;
                if (bodyPhase)
                {
                    phaseRemaining = _options.EffectiveResponseBodyTimeout - responseHeadersClock.Elapsed;
                }
                else if (!trace.IsUploadCompleted)
                {
                    var uploadRemaining = _options.EffectiveUploadTimeout - trace.UploadElapsed;
                    var idleRemaining = _options.EffectiveUploadIdleTimeout - trace.UploadIdleElapsed;
                    phaseRemaining = Min(uploadRemaining, idleRemaining);
                }
                else
                {
                    if (!responseHeadersClockStarted)
                    {
                        responseHeadersClock.Restart();
                        responseHeadersClockStarted = true;
                    }

                    phaseRemaining = _options.EffectiveResponseHeadersTimeout - responseHeadersClock.Elapsed;
                }

                if (phaseRemaining <= TimeSpan.Zero)
                {
                    CancelForDeadline();
                    throw new TimeoutException("AI request phase deadline elapsed.");
                }

                var wait = Min(totalRemaining, phaseRemaining, PollInterval);
                var delay = Task.Delay(wait, _callerToken);
                var completed = await Task.WhenAny(operation, delay).ConfigureAwait(false);
                if (completed == operation)
                {
                    return await operation.ConfigureAwait(false);
                }

                // A cancelled delay is observed by the next token check. The
                // provider task is intentionally left for the caller's
                // deferred observer when cancellation/timeout wins.
                _callerToken.ThrowIfCancellationRequested();
            }
        }

        private static TimeSpan Min(params TimeSpan[] values) => values.Min();

        public void DeferSendCleanup(
            Task<HttpResponseMessage> sendTask,
            Action<HttpResponseMessage>? lateResponseHeaders = null,
            Action<Exception>? lateFailure = null)
        {
            if (Interlocked.CompareExchange(ref _cleanupScheduled, 1, 0) != 0)
            {
                return;
            }

            _ = ObserveSendAndDisposeAsync(sendTask, lateResponseHeaders, lateFailure);
        }

        public void DeferBodyCleanup(
            Task<byte[]> bodyTask,
            Action<byte[]>? lateBodyCompleted = null,
            Action<Exception>? lateFailure = null)
        {
            if (Interlocked.CompareExchange(ref _cleanupScheduled, 1, 0) != 0)
            {
                return;
            }

            _ = ObserveBodyAndDisposeAsync(bodyTask, lateBodyCompleted, lateFailure);
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _cleanupScheduled, 1, 0) == 0)
            {
                DisposeResources();
            }
        }

        private void CancelForDeadline()
        {
            if (Interlocked.Exchange(ref _deadlineCancellationRequested, 1) != 0)
            {
                return;
            }

            try
            {
                _transportCancellation.Cancel();
            }
            catch
            {
                // Cancellation callbacks are provider/handler-owned. The
                // hard deadline is already decided; callback failures must not
                // delay or replace the typed timeout result.
            }
        }

        private async Task ObserveSendAndDisposeAsync(
            Task<HttpResponseMessage> sendTask,
            Action<HttpResponseMessage>? lateResponseHeaders,
            Action<Exception>? lateFailure)
        {
            try
            {
                var response = await sendTask.ConfigureAwait(false);
                lateResponseHeaders?.Invoke(response);
                response?.Dispose();
            }
            catch (Exception exception)
            {
                // The foreground path already returned a typed unknown result;
                // this observer exists only to consume late task failures.
                lateFailure?.Invoke(exception);
            }
            finally
            {
                DisposeResources();
            }
        }

        private async Task ObserveBodyAndDisposeAsync(
            Task<byte[]> bodyTask,
            Action<byte[]>? lateBodyCompleted,
            Action<Exception>? lateFailure)
        {
            try
            {
                var body = await bodyTask.ConfigureAwait(false);
                lateBodyCompleted?.Invoke(body);
            }
            catch (Exception exception)
            {
                // See ObserveSendAndDisposeAsync: late body failures are
                // intentionally observed and never surface as unhandled task
                // exceptions.
                lateFailure?.Invoke(exception);
            }
            finally
            {
                DisposeResources();
            }
        }

        private void DisposeResources()
        {
            try { _response?.Dispose(); }
            catch { }
            try { _message.Dispose(); }
            catch { }
            try { _transportCancellation.Dispose(); }
            catch { }
        }
    }

    private static async Task<byte[]> ReadLimitedResponseAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var remaining = maxBytes - (int)output.Length;
            if (remaining < 0) throw new InvalidDataException("AI_PROVIDER_RESPONSE_TOO_LARGE");
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maxBytes)
                throw new InvalidDataException("AI_PROVIDER_RESPONSE_TOO_LARGE");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

}
