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

public sealed record VolcArkAiOptions(
    string ModelId = "doubao-seed-2-1-pro-260628",
    string Endpoint = "https://ark.cn-beijing.volces.com/api/v3/responses",
    TimeSpan? RequestTimeout = null,
    int MaxRequestBytes = 16 * 1024 * 1024,
    int MaxResponseBytes = 1 * 1024 * 1024,
    int MaxAssetsPerRequest = AiAnalysisProductionPolicy.MaxTargetAssetsPerRequest,
    bool ReleaseCopiesAfterRequest = true,
    Action<AiResponseShapeDiagnostic>? ResponseShapeDiagnosticSink = null,
    TimeSpan? UploadTimeout = null,
    TimeSpan? UploadIdleTimeout = null,
    TimeSpan? ResponseHeadersTimeout = null,
    TimeSpan? ResponseBodyTimeout = null,
    int MaxReferenceAssetsPerRequest = AiAnalysisProductionPolicy.MaxReferenceAssetsPerRequest)
{
    /// <summary>
    /// Total wall-clock budget measured from the start of HTTP submission.
    /// Stage budgets below are independent caps, and this total budget still
    /// prevents a sequence of slow stages from waiting without bound.
    /// </summary>
    public TimeSpan EffectiveRequestTimeout => RequestTimeout ?? TimeSpan.FromSeconds(300);

    /// <summary>Absolute time allowed for writing the request body.</summary>
    ///
    /// The total request deadline remains the outer safety boundary.  A 240 s
    /// upload allowance leaves room for the response phases while accommodating
    /// the measured slow-uplink case (about 8.4 MB at roughly 0.34 Mbps).
    public TimeSpan EffectiveUploadTimeout => UploadTimeout ?? TimeSpan.FromSeconds(240);

    /// <summary>Maximum period without an upload progress callback.</summary>
    ///
    /// This is deliberately longer than the previous 45 s guard.  A blocked
    /// socket write is not evidence that the provider accepted the request, and
    /// the measured network can pause for longer than 45 s before the next
    /// completed write.  The absolute upload and total request deadlines still
    /// bound the wait.
    public TimeSpan EffectiveUploadIdleTimeout => UploadIdleTimeout ?? TimeSpan.FromSeconds(90);

    /// <summary>Time allowed to receive response headers after upload.</summary>
    public TimeSpan EffectiveResponseHeadersTimeout => ResponseHeadersTimeout ?? TimeSpan.FromSeconds(120);

    /// <summary>Time allowed to read the response body after headers.</summary>
    public TimeSpan EffectiveResponseBodyTimeout => ResponseBodyTimeout ?? TimeSpan.FromSeconds(60);
}

/// <summary>
/// Bounded response-shape evidence for opt-in troubleshooting.  This record
/// intentionally contains no response values, IDs, media, paths or secrets.
/// </summary>
public sealed record AiResponseShapeDiagnostic(
    int ResponseBytes,
    string RootType,
    int TopLevelFieldCount,
    IReadOnlyList<string> TopLevelFields,
    string StatusClass,
    bool HasOutputText,
    IReadOnlyList<string> OutputBlockTypes,
    string FailureStage,
    string? FirstSchemaFailurePath,
    string? FirstSchemaFailureKeyword);

/// <summary>
/// Supplier-neutral result contract used by the Application port. The
/// adapter deliberately returns a typed outcome instead of throwing provider
/// exceptions so callers can persist unknown outcomes without guessing.
/// </summary>
public sealed class VolcArkAiAdapter : IAiServicePort
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IAiCredentialSource _credentialSource;
    private readonly IAiAnalysisCopySource _copySource;
    private readonly IJsonContractValidator _contractValidator;
    private readonly VolcArkAiOptions _options;
    private readonly IRedactedLogger? _transportLogger;

    public VolcArkAiAdapter(
        HttpClient httpClient,
        IAiCredentialSource credentialSource,
        IAiAnalysisCopySource copySource,
        IJsonContractValidator contractValidator,
        VolcArkAiOptions? options = null,
        IRedactedLogger? transportLogger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credentialSource = credentialSource ?? throw new ArgumentNullException(nameof(credentialSource));
        _copySource = copySource ?? throw new ArgumentNullException(nameof(copySource));
        _contractValidator = contractValidator ?? throw new ArgumentNullException(nameof(contractValidator));
        _options = options ?? new VolcArkAiOptions();
        _transportLogger = transportLogger;
        ValidateOptions(_options);
    }

    public async Task<AiAnalysisExecutionResult> AnalyzeAsync(
        AiAnalysisRequestV1 request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            ValidateRequest(request, _options.MaxAssetsPerRequest);
            cancellationToken.ThrowIfCancellationRequested();

            string? credential;
            try
            {
                credential = await _credentialSource.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Failure(AiProviderOutcome.Cancelled, "AI_REQUEST_CANCELLED", false, "分析已取消。", stopwatch);
            }
            catch (Exception exception) when (
                exception is IOException
                    or InvalidDataException
                    or InvalidOperationException
                    or UnauthorizedAccessException
                    or System.ComponentModel.Win32Exception)
            {
                // Credential Manager/storage failures happen before an HTTP
                // request exists. Expose a stable actionable code instead of
                // allowing a platform exception to escape to the scheduler
                // and be misclassified as an in-flight unknown result. Never
                // include the native error text, target name or credential.
                return Failure(
                    AiProviderOutcome.TerminalError,
                    "AI_PROVIDER_CREDENTIAL_UNAVAILABLE",
                    false,
                    "无法读取本机 AI 凭据，请检查凭据管理器后重试。",
                    stopwatch);
            }
            if (string.IsNullOrWhiteSpace(credential))
            {
                return Failure(
                    AiProviderOutcome.TerminalError,
                    "AI_PROVIDER_CREDENTIAL_MISSING",
                    retryable: false,
                    "请在本机凭据管理器配置 AI 测试凭据后重试。",
                    stopwatch);
            }

            // Header values must be a single opaque credential. Reject
            // malformed Credential Manager entries before constructing an
            // Authorization header, without logging or echoing the secret.
            if (credential.Any(character => character is '\r' or '\n' or '\0'))
            {
                return Failure(
                    AiProviderOutcome.TerminalError,
                    "AI_PROVIDER_CREDENTIAL_INVALID",
                    retryable: false,
                    "本机 AI 凭据格式无效，请在凭据管理器中重新保存单行 API Key。",
                    stopwatch);
            }

            var images = new List<AiAnalysisCopy>(request.Assets.Count);
            try
            {
                foreach (var asset in request.Assets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var copy = await _copySource.OpenAsync(asset.AnalysisCopyReference, cancellationToken)
                            .ConfigureAwait(false);
                        if (copy.Content.IsEmpty)
                        {
                            throw new InvalidDataException("AI 分析副本为空。");
                        }

                        ValidateCopy(request, asset, copy);

                        images.Add(copy);
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException)
                    {
                        return Failure(AiProviderOutcome.TerminalError, "AI_ANALYSIS_COPY_UNAVAILABLE", false, "分析副本不可用，请重新扫描素材后重试。", stopwatch);
                    }
                }

                // Build exactly the bytes that will be assigned to the HTTP
                // content.  The serialized UTF-8 JSON length is authoritative;
                // source lengths and Base64 estimates can differ materially
                // (especially for PNG), so they must never reject a request or
                // stand in for this final preflight.
                var payload = BuildProviderPayload(request, images);
                var trace = new AiTransportTrace(
                    _transportLogger,
                    request.RequestId,
                    "ai-analysis/v1",
                    stopwatch,
                    payload.Length,
                    request.Assets.Count,
                    request.Requirements.Count,
                    _options);
                trace.MarkPayloadReady();
                if (payload.Length > _options.MaxRequestBytes)
                {
                    trace.MarkFailure("AI_PROVIDER_REQUEST_TOO_LARGE");
                    return Failure(
                        AiProviderOutcome.TerminalError,
                        "AI_PROVIDER_REQUEST_TOO_LARGE",
                        retryable: false,
                        RequestTooLargeUserAction(payload.Length),
                        stopwatch);
                }

                var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
                {
                    Content = new ObservableByteArrayContent(
                        payload,
                        trace.MarkUploadProgress,
                        trace.MarkUploadCompleted)
                };
                ConfigureTransportRequest(message);
                message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                // The credential is scoped to this request and is never put in
                // a shared HttpClient header or a persisted object.
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);

                var scope = new HttpRequestScope(message, _options, cancellationToken);
                try
                {
                    Task<HttpResponseMessage> sendTask;
                    try
                    {
                        trace.MarkSendStarted();
                        sendTask = _httpClient.SendAsync(
                            message,
                            HttpCompletionOption.ResponseHeadersRead,
                            scope.TransportToken);
                    }
                    catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
                    {
                        trace.MarkFailure("AI_REQUEST_CANCELLED", exception);
                        return Failure(AiProviderOutcome.Cancelled, "AI_REQUEST_CANCELLED", false, "分析已取消。", stopwatch);
                    }
                    catch (OperationCanceledException exception)
                    {
                        var failure = trace.CreateTimeoutFailure(exception);
                        return Failure(failure.Outcome, failure.Code, failure.Retryable, failure.UserAction, stopwatch);
                    }
                    catch (HttpRequestException exception)
                    {
                        var failure = trace.CreateNetworkFailure(exception);
                        return Failure(failure.Outcome, failure.Code, failure.Retryable, failure.UserAction, stopwatch);
                    }
                    catch (IOException exception)
                    {
                        var failure = trace.CreateNetworkFailure(exception);
                        return Failure(failure.Outcome, failure.Code, failure.Retryable, failure.UserAction, stopwatch);
                    }

                    HttpResponseMessage response;
                    try
                    {
                        response = await scope.WaitForResponseHeadersAsync(sendTask, trace).ConfigureAwait(false);
                    }
                    catch (TimeoutException exception)
                    {
                        scope.DeferSendCleanup(sendTask, trace.MarkLateResponseHeaders, trace.MarkLateFailure);
                        var failure = trace.CreateTimeoutFailure(exception);
                        return Failure(failure.Outcome, failure.Code, failure.Retryable, failure.UserAction, stopwatch);
                    }
                    catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
                    {
                        scope.DeferSendCleanup(sendTask, trace.MarkLateResponseHeaders, trace.MarkLateFailure);
                        trace.MarkFailure("AI_REQUEST_CANCELLED", exception);
                        return Failure(AiProviderOutcome.Cancelled, "AI_REQUEST_CANCELLED", false, "分析已取消。", stopwatch);
                    }
                    catch (OperationCanceledException exception)
                    {
                        scope.DeferSendCleanup(sendTask, trace.MarkLateResponseHeaders, trace.MarkLateFailure);
                        var failure = trace.CreateTimeoutFailure(exception);
                        return Failure(failure.Outcome, failure.Code, failure.Retryable, failure.UserAction, stopwatch);
                    }
                    catch (HttpRequestException exception)
                    {
                        scope.DeferSendCleanup(sendTask, trace.MarkLateResponseHeaders, trace.MarkLateFailure);
                        var failure = trace.CreateNetworkFailure(exception);
                        return Failure(failure.Outcome, failure.Code, failure.Retryable, failure.UserAction, stopwatch);
                    }
                    catch (IOException exception)
                    {
                        scope.DeferSendCleanup(sendTask, trace.MarkLateResponseHeaders, trace.MarkLateFailure);
                        var failure = trace.CreateNetworkFailure(exception);
                        return Failure(failure.Outcome, failure.Code, failure.Retryable, failure.UserAction, stopwatch);
                    }

                    scope.AttachResponse(response);
                    trace.MarkResponseHeaders(response);
                    if (!response.IsSuccessStatusCode)
                    {
                        var failure = MapHttpFailure(response.StatusCode, stopwatch);
                        trace.MarkFailure(failure.Error?.Code ?? "AI_PROVIDER_HTTP_ERROR");
                        return failure;
                    }

                    if (response.Content.Headers.ContentLength is > 0 and var contentLength
                        && contentLength > _options.MaxResponseBytes)
                    {
                        trace.MarkFailure("AI_PROVIDER_RESPONSE_TOO_LARGE");
                        return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_TOO_LARGE", false, "供应商响应过大，已安全拒绝。", stopwatch);
                    }

                    trace.MarkResponseBodyStarted();
                    var bodyTask = ReadLimitedResponseAsync(response, _options.MaxResponseBytes, scope.TransportToken);
                    byte[] body;
                    try
                    {
                        body = await scope.WaitForResponseBodyAsync(bodyTask, trace).ConfigureAwait(false);
                        trace.MarkResponseBodyCompleted(body);
                    }
                    catch (TimeoutException exception)
                    {
                        scope.DeferBodyCleanup(bodyTask, trace.MarkLateBodyCompleted, trace.MarkLateFailure);
                        var failure = trace.CreateTimeoutFailure(exception);
                        return Failure(failure.Outcome, failure.Code, failure.Retryable, failure.UserAction, stopwatch);
                    }
                    catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
                    {
                        scope.DeferBodyCleanup(bodyTask, trace.MarkLateBodyCompleted, trace.MarkLateFailure);
                        trace.MarkFailure("AI_REQUEST_CANCELLED", exception);
                        return Failure(AiProviderOutcome.Cancelled, "AI_REQUEST_CANCELLED", false, "分析已取消。", stopwatch);
                    }
                    catch (OperationCanceledException exception)
                    {
                        scope.DeferBodyCleanup(bodyTask, trace.MarkLateBodyCompleted, trace.MarkLateFailure);
                        var failure = trace.CreateTimeoutFailure(exception);
                        return Failure(failure.Outcome, failure.Code, failure.Retryable, failure.UserAction, stopwatch);
                    }
                    catch (HttpRequestException exception)
                    {
                        scope.DeferBodyCleanup(bodyTask, trace.MarkLateBodyCompleted, trace.MarkLateFailure);
                        var failure = trace.CreateNetworkFailure(exception);
                        return Failure(failure.Outcome, failure.Code, failure.Retryable, failure.UserAction, stopwatch);
                    }
                    catch (IOException exception)
                    {
                        scope.DeferBodyCleanup(bodyTask, trace.MarkLateBodyCompleted, trace.MarkLateFailure);
                        var failure = trace.CreateNetworkFailure(exception);
                        return Failure(failure.Outcome, failure.Code, failure.Retryable, failure.UserAction, stopwatch);
                    }
                    catch (InvalidDataException exception)
                    {
                        trace.MarkFailure("AI_PROVIDER_RESPONSE_TOO_LARGE", exception);
                        return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_TOO_LARGE", false, "供应商响应过大，已安全拒绝。", stopwatch);
                    }

                    return ParseProviderResponse(request, body, stopwatch);
                }
                finally
                {
                    // HttpRequestScope is cancellation-safe: after a hard
                    // deadline it defers disposal until the provider task (or
                    // the response-body task) has been observed, so an
                    // uncancellable custom/native handler cannot retain an
                    // unobserved exception or use a disposed request message.
                    scope.Dispose();
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                return Failure(AiProviderOutcome.TerminalError, "AI_ANALYSIS_COPY_UNAVAILABLE", false, "分析副本不可用，请重新扫描素材后重试。", stopwatch);
            }
            finally
            {
                // Copy lifetime is owned by the application lease/coordinator.
                // The provider adapter never deletes a reference: retries,
                // refinement and restart may still need the same copy.
                // Legacy unleased test copies retain the historical opt-in cleanup
                // for compatibility; formally prepared copies always carry a lease
                // and are left to the application owner.
                if (_options.ReleaseCopiesAfterRequest)
                {
                    foreach (var copy in images.Where(copy => copy.LeaseId is null))
                    {
                        try { await _copySource.ReleaseAsync(copy.Reference, CancellationToken.None).ConfigureAwait(false); }
                        catch { /* cleanup is recoverable application-owned state */ }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(AiProviderOutcome.Cancelled, "AI_REQUEST_CANCELLED", false, "分析已取消。", stopwatch);
        }
        catch (InvalidOperationException)
        {
            return Failure(AiProviderOutcome.TerminalError, "AI_REQUEST_INVALID", false, "分析请求不符合当前版本要求，请重新扫描素材后重试。", stopwatch);
        }
    }

    public async Task<ReferenceLearningAiExecutionResult> AnalyzeReferenceLearningAsync(
        ReferenceLearningAiRequestV2 request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            ValidateReferenceLearningRequest(request, _options.MaxReferenceAssetsPerRequest);
            if (!_contractValidator.Validate(
                    ContractDocumentKind.ReferenceLearningAi,
                    JsonSerializer.Serialize(request, JsonOptions)).IsValid)
                throw new InvalidOperationException("TPL_LEARN_AI_REQUEST_SCHEMA_INVALID");
            cancellationToken.ThrowIfCancellationRequested();

            string? credential;
            try
            {
                credential = await _credentialSource.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ReferenceFailure(AiProviderOutcome.Cancelled, "AI_REQUEST_CANCELLED", false, "分析已取消。", stopwatch);
            }
            catch (Exception exception) when (exception is IOException
                or InvalidDataException
                or InvalidOperationException
                or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
            {
                return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_CREDENTIAL_UNAVAILABLE", false, "无法读取本机 AI 凭据，请检查凭据管理器后重试。", stopwatch);
            }

            if (string.IsNullOrWhiteSpace(credential))
            {
                return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_CREDENTIAL_MISSING", false, "请在设置中配置 AI 凭据后重试。", stopwatch);
            }
            if (credential.Any(character => character is '\r' or '\n' or '\0'))
            {
                return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_CREDENTIAL_INVALID", false, "本机 AI 凭据格式无效，请重新保存单行 API Key。", stopwatch);
            }

            var images = new List<AiAnalysisCopy>(request.Assets.Count);
            try
            {
                foreach (var asset in request.Assets)
                {
                    AiAnalysisCopy copy;
                    try
                    {
                        copy = await _copySource.OpenAsync(asset.AnalysisCopyReference, cancellationToken).ConfigureAwait(false);
                        ValidateReferenceCopy(request, asset, copy);
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException)
                    {
                        return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_ANALYSIS_COPY_UNAVAILABLE", false, "分析副本不可用，请重新扫描素材后重试。", stopwatch);
                    }
                    images.Add(copy);
                }

                var payload = BuildReferenceLearningPayload(request, images);
                var trace = new AiTransportTrace(
                    _transportLogger,
                    request.RequestId,
                    "reference-learning-ai/v2",
                    stopwatch,
                    payload.Length,
                    request.Assets.Count,
                    request.Frames.Count,
                    _options);
                trace.MarkPayloadReady();
                if (payload.Length > _options.MaxRequestBytes)
                {
                    trace.MarkFailure("AI_PROVIDER_REQUEST_TOO_LARGE");
                    return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_REQUEST_TOO_LARGE", false, RequestTooLargeUserAction(payload.Length), stopwatch);
                }

                var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
                {
                    Content = new ObservableByteArrayContent(
                        payload,
                        trace.MarkUploadProgress,
                        trace.MarkUploadCompleted)
                };
                ConfigureTransportRequest(message);
                message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);

                var scope = new HttpRequestScope(message, _options, cancellationToken);
                HttpResponseMessage response;
                try
                {
                    Task<HttpResponseMessage> sendTask;
                    try
                    {
                        trace.MarkSendStarted();
                        sendTask = _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, scope.TransportToken);
                    }
                    catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
                    {
                        trace.MarkFailure("AI_REQUEST_CANCELLED", exception);
                        return ReferenceFailure(AiProviderOutcome.Cancelled, "AI_REQUEST_CANCELLED", false, "分析已取消。", stopwatch);
                    }
                    catch (OperationCanceledException exception)
                    {
                        var failure = trace.CreateTimeoutFailure(exception);
                        return ReferenceFailure(failure.Outcome, failure.Code, failure.Retryable, failure.UserAction, stopwatch);
                    }
                    catch (HttpRequestException exception)
                    {
                        var failure = trace.CreateNetworkFailure(exception);
                        return ReferenceFailure(failure.Outcome, failure.Code, failure.Retryable, failure.UserAction, stopwatch);
                    }
                    catch (IOException exception)
                    {
                        var failure = trace.CreateNetworkFailure(exception);
                        return ReferenceFailure(failure.Outcome, failure.Code, failure.Retryable, failure.UserAction, stopwatch);
                    }

                    try
                    {
                        response = await scope.WaitForResponseHeadersAsync(sendTask, trace).ConfigureAwait(false);
                    }
                    catch (TimeoutException exception)
                    {
                        scope.DeferSendCleanup(sendTask, trace.MarkLateResponseHeaders, trace.MarkLateFailure);
                        var failure = trace.CreateTimeoutFailure(exception);
                        return ReferenceFailure(failure.Outcome, failure.Code, failure.Retryable, failure.UserAction, stopwatch);
                    }
                    catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
                    {
                        scope.DeferSendCleanup(sendTask, trace.MarkLateResponseHeaders, trace.MarkLateFailure);
                        trace.MarkFailure("AI_REQUEST_CANCELLED", exception);
                        return ReferenceFailure(AiProviderOutcome.Cancelled, "AI_REQUEST_CANCELLED", false, "分析已取消。", stopwatch);
                    }
                    catch (OperationCanceledException exception)
                    {
                        scope.DeferSendCleanup(sendTask, trace.MarkLateResponseHeaders, trace.MarkLateFailure);
                        var failure = trace.CreateTimeoutFailure(exception);
                        return ReferenceFailure(failure.Outcome, failure.Code, failure.Retryable, failure.UserAction, stopwatch);
                    }
                    catch (HttpRequestException exception)
                    {
                        scope.DeferSendCleanup(sendTask, trace.MarkLateResponseHeaders, trace.MarkLateFailure);
                        var failure = trace.CreateNetworkFailure(exception);
                        return ReferenceFailure(failure.Outcome, failure.Code, failure.Retryable, failure.UserAction, stopwatch);
                    }
                    catch (IOException exception)
                    {
                        scope.DeferSendCleanup(sendTask, trace.MarkLateResponseHeaders, trace.MarkLateFailure);
                        var failure = trace.CreateNetworkFailure(exception);
                        return ReferenceFailure(failure.Outcome, failure.Code, failure.Retryable, failure.UserAction, stopwatch);
                    }

                    scope.AttachResponse(response);
                    trace.MarkResponseHeaders(response);
                    if (!response.IsSuccessStatusCode)
                    {
                        var failure = MapReferenceHttpFailure(response.StatusCode, stopwatch);
                        trace.MarkFailure(failure.Error?.Code ?? "AI_PROVIDER_HTTP_ERROR");
                        return failure;
                    }

                    if (response.Content.Headers.ContentLength is > 0 and var contentLength
                        && contentLength > _options.MaxResponseBytes)
                    {
                        trace.MarkFailure("AI_PROVIDER_RESPONSE_TOO_LARGE");
                        return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_TOO_LARGE", false, "供应商响应过大，已安全拒绝。", stopwatch);
                    }

                    trace.MarkResponseBodyStarted();
                    var bodyTask = ReadLimitedResponseAsync(response, _options.MaxResponseBytes, scope.TransportToken);
                    byte[] body;
                    try
                    {
                        body = await scope.WaitForResponseBodyAsync(bodyTask, trace).ConfigureAwait(false);
                        trace.MarkResponseBodyCompleted(body);
                    }
                    catch (TimeoutException exception)
                    {
                        scope.DeferBodyCleanup(bodyTask, trace.MarkLateBodyCompleted, trace.MarkLateFailure);
                        var failure = trace.CreateTimeoutFailure(exception);
                        return ReferenceFailure(failure.Outcome, failure.Code, failure.Retryable, failure.UserAction, stopwatch);
                    }
                    catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
                    {
                        scope.DeferBodyCleanup(bodyTask, trace.MarkLateBodyCompleted, trace.MarkLateFailure);
                        trace.MarkFailure("AI_REQUEST_CANCELLED", exception);
                        return ReferenceFailure(AiProviderOutcome.Cancelled, "AI_REQUEST_CANCELLED", false, "分析已取消。", stopwatch);
                    }
                    catch (OperationCanceledException exception)
                    {
                        scope.DeferBodyCleanup(bodyTask, trace.MarkLateBodyCompleted, trace.MarkLateFailure);
                        var failure = trace.CreateTimeoutFailure(exception);
                        return ReferenceFailure(failure.Outcome, failure.Code, failure.Retryable, failure.UserAction, stopwatch);
                    }
                    catch (HttpRequestException exception)
                    {
                        scope.DeferBodyCleanup(bodyTask, trace.MarkLateBodyCompleted, trace.MarkLateFailure);
                        var failure = trace.CreateNetworkFailure(exception);
                        return ReferenceFailure(failure.Outcome, failure.Code, failure.Retryable, failure.UserAction, stopwatch);
                    }
                    catch (IOException exception)
                    {
                        scope.DeferBodyCleanup(bodyTask, trace.MarkLateBodyCompleted, trace.MarkLateFailure);
                        var failure = trace.CreateNetworkFailure(exception);
                        return ReferenceFailure(failure.Outcome, failure.Code, failure.Retryable, failure.UserAction, stopwatch);
                    }
                    catch (InvalidDataException exception)
                    {
                        trace.MarkFailure("AI_PROVIDER_RESPONSE_TOO_LARGE", exception);
                        return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_TOO_LARGE", false, "供应商响应过大，已安全拒绝。", stopwatch);
                    }

                    return ParseReferenceLearningResponse(request, body, stopwatch);
                }
                finally
                {
                    scope.Dispose();
                }
            }
            finally
            {
                if (_options.ReleaseCopiesAfterRequest)
                {
                    foreach (var copy in images.Where(copy => copy.LeaseId is null))
                    {
                        try { await _copySource.ReleaseAsync(copy.Reference, CancellationToken.None).ConfigureAwait(false); }
                        catch { }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ReferenceFailure(AiProviderOutcome.Cancelled, "AI_REQUEST_CANCELLED", false, "分析已取消。", stopwatch);
        }
        catch (InvalidOperationException)
        {
            return ReferenceFailure(AiProviderOutcome.TerminalError, "TPL_LEARN_AI_REQUEST_INVALID", false, "语义分析请求不符合 v2 合同，已安全拒绝。", stopwatch);
        }
    }

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

    private static void ValidateCopy(
        AiAnalysisRequestV1 request,
        AiAssetInputV1 asset,
        AiAnalysisCopy copy)
    {
        if (!string.Equals(copy.Reference, asset.AnalysisCopyReference, StringComparison.Ordinal)
            || !IsSupportedMime(copy.MimeType)
            || copy.PixelWidth <= 0 || copy.PixelHeight <= 0)
            throw new InvalidDataException("AI_ANALYSIS_COPY_METADATA_INVALID");
        var digest = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(copy.Content.Span)).ToLowerInvariant();
        if (copy.ContentDigest is null
            || !string.Equals(copy.ContentDigest, digest, StringComparison.Ordinal))
            throw new InvalidDataException("AI_ANALYSIS_COPY_DIGEST_MISMATCH");
        if (copy.ByteLength is null || copy.ByteLength != copy.Content.Length)
            throw new InvalidDataException("AI_ANALYSIS_COPY_LENGTH_MISMATCH");
        if (asset.AnalysisCopyDigest is null
            || !string.Equals(asset.AnalysisCopyDigest, digest, StringComparison.Ordinal))
            throw new InvalidDataException("AI_ANALYSIS_COPY_DIGEST_MISMATCH");
        if (asset.AnalysisCopyLength is null || asset.AnalysisCopyLength != copy.Content.Length)
            throw new InvalidDataException("AI_ANALYSIS_COPY_LENGTH_MISMATCH");
        if (asset.AnalysisCopyMimeType is null
            || !string.Equals(asset.AnalysisCopyMimeType, copy.MimeType, StringComparison.Ordinal))
            throw new InvalidDataException("AI_ANALYSIS_COPY_MIME_MISMATCH");
        if (asset.AnalysisCopyCompressionProfile is null
            || copy.CompressionProfileVersion is null
            || !string.Equals(request.CompressionProfileVersion, asset.AnalysisCopyCompressionProfile, StringComparison.Ordinal)
            || !string.Equals(asset.AnalysisCopyCompressionProfile, copy.CompressionProfileVersion, StringComparison.Ordinal))
            throw new InvalidDataException("AI_ANALYSIS_COPY_PROFILE_MISMATCH");
    }

    private static bool IsSupportedMime(string value) => value is "image/jpeg" or "image/png";

    private AiAnalysisExecutionResult ParseProviderResponse(
        AiAnalysisRequestV1 request,
        byte[] body,
        Stopwatch stopwatch)
    {
        var shape = new ResponseShapeCollector(body.Length);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            EmitDiagnostic(shape.Build("json_parse"));
            return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商返回的数据无法校验，已进入待处理状态。", stopwatch);
        }

        using (document)
        {
            var root = document.RootElement;
            shape.SetRoot(root);
            if (root.ValueKind != JsonValueKind.Object)
            {
                EmitDiagnostic(shape.Build("root_type"));
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商返回的数据无法校验，已进入待处理状态。", stopwatch);
            }

            var status = root.TryGetProperty("status", out var statusElement)
                && statusElement.ValueKind == JsonValueKind.String
                ? statusElement.GetString()
                : null;
            shape.SetStatus(ClassifyStatus(status));
            shape.SetOutputShape(root);
            var usage = ParseProviderUsage(root);
            if (status is "in_progress" or "queued" or "processing")
            {
                EmitDiagnostic(shape.Build("status_pending"));
                return Failure(AiProviderOutcome.AiResultUnknown, "AI_PROVIDER_RESULT_PENDING", false, "供应商仍在处理，当前结果未知，请稍后查询。", stopwatch, usage);
            }

            if (string.Equals(status, "incomplete", StringComparison.Ordinal))
            {
                EmitDiagnostic(shape.Build("status_incomplete"));
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_OUTPUT_INCOMPLETE", false, "供应商未完整生成结构化结果，当前响应不能使用；请人工检查后再决定是否建立新请求。", stopwatch, usage);
            }

            if (string.Equals(status, "failed", StringComparison.Ordinal))
            {
                EmitDiagnostic(shape.Build("status_failed"));
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_REQUEST_FAILED", false, "供应商明确返回请求失败；请检查任务后再决定是否建立新请求。", stopwatch, usage);
            }

            if (status is "cancelled" or "canceled")
            {
                EmitDiagnostic(shape.Build("status_cancelled"));
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_REQUEST_CANCELLED", false, "供应商明确返回请求已取消；请检查任务后再决定是否建立新请求。", stopwatch, usage);
            }

            if (status is not ("completed" or "complete"))
            {
                EmitDiagnostic(shape.Build("status_invalid"));
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_STATUS_INVALID", false, "供应商返回了无法确认的请求状态，已安全拒绝。", stopwatch, usage);
            }

            var outputText = ExtractOutputText(root);
            if (string.IsNullOrWhiteSpace(outputText))
            {
                EmitDiagnostic(shape.Build("missing_output_text"));
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商未返回结构化分析结果。", stopwatch, usage);
            }

            JsonObject resultObject;
            try
            {
                resultObject = JsonNode.Parse(outputText)?.AsObject()
                    ?? throw new JsonException();
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                EmitDiagnostic(shape.Build("output_json_parse"));
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商返回的数据不是有效 JSON。", stopwatch, usage);
            }

            var providerReference = ReadSafeProviderReference(root);
            if (!resultObject.ContainsKey("provider_request_ref"))
            {
                resultObject["provider_request_ref"] = providerReference;
            }

            usage ??= ParseResultUsage(resultObject);
            if (!resultObject.ContainsKey("usage") && usage is not null)
            {
                resultObject["usage"] = new JsonObject
                {
                    ["usage_event_id"] = "usage_" + request.RequestId,
                    ["input_units"] = usage.InputTokens,
                    ["output_units"] = usage.OutputTokens,
                    ["request_count"] = 1,
                    ["provider_cost_amount"] = null,
                    ["provider_cost_currency"] = null
                };
            }

            var serialized = resultObject.ToJsonString(JsonOptions);
            ContractValidationResult validation;
            string? firstSchemaFailurePath = null;
            string? firstSchemaFailureKeyword = null;
            if (_contractValidator is IJsonContractDiagnosticValidator diagnosticValidator)
            {
                var diagnostic = diagnosticValidator.ValidateWithDiagnostic(ContractDocumentKind.AiAnalysis, serialized);
                validation = diagnostic.Validation;
                firstSchemaFailurePath = diagnostic.FirstFailureInstancePath;
                firstSchemaFailureKeyword = diagnostic.FirstFailureKeyword;
            }
            else
            {
                validation = _contractValidator.Validate(ContractDocumentKind.AiAnalysis, serialized);
            }

            if (!validation.IsValid)
            {
                EmitDiagnostic(shape.Build("schema", firstSchemaFailurePath, firstSchemaFailureKeyword));
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商返回的数据不符合结构化合同。", stopwatch, usage);
            }

            AiAnalysisResultV1 result;
            try
            {
                result = JsonSerializer.Deserialize<AiAnalysisResultV1>(serialized, JsonOptions)
                    ?? throw new JsonException();
            }
            catch (JsonException)
            {
                EmitDiagnostic(shape.Build("result_deserialize"));
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商返回的数据无法读取。", stopwatch, usage);
            }

            if (!(string.Equals(result.RequestId, request.RequestId, StringComparison.Ordinal)
                    || string.Equals(
                        result.RequestId,
                        AiAnalysisResultReferenceNormalizer.RequestAlias,
                        StringComparison.Ordinal))
                || !(string.Equals(result.ModelConfigVersion, request.ModelConfigVersion, StringComparison.Ordinal)
                    || string.Equals(
                        result.ModelConfigVersion,
                        AiAnalysisResultReferenceNormalizer.ModelConfigVersionAlias,
                        StringComparison.Ordinal)))
            {
                EmitDiagnostic(shape.Build("reference_mismatch"));
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_REFERENCE_MISMATCH", false, "供应商结果与当前分析请求不匹配。", stopwatch, usage);
            }

            try
            {
                result = AiAnalysisResultReferenceNormalizer.Normalize(request, result);
            }
            catch (InvalidDataException exception) when (AiAnalysisResultSemanticValidator.IsSemanticError(exception))
            {
                EmitDiagnostic(shape.Build("semantic"));
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商返回的分析结果结构不一致，已安全拒绝。", stopwatch, usage);
            }
            catch (InvalidDataException)
            {
                EmitDiagnostic(shape.Build("reference_closure"));
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_REFERENCE_MISMATCH", false, "供应商结果引用了当前请求之外的图片或图片框。", stopwatch, usage);
            }

            EmitDiagnostic(shape.Build("none"));
            stopwatch.Stop();
            return AiAnalysisExecutionResult.Completed(result, usage, stopwatch.Elapsed);
        }
    }

    private void EmitDiagnostic(AiResponseShapeDiagnostic diagnostic)
    {
        var sink = _options.ResponseShapeDiagnosticSink;
        if (sink is null)
        {
            return;
        }

        try
        {
            sink(diagnostic);
        }
        catch
        {
            // Diagnostics are strictly best-effort and must never alter the
            // provider outcome or expose callback failures to users.
        }
    }

    private static string ClassifyStatus(string? status) => status switch
    {
        "completed" or "complete" => "completed",
        "in_progress" or "queued" or "processing" => "pending",
        "incomplete" => "incomplete",
        "failed" or "cancelled" or "canceled" => "terminal",
        null or "" => "missing",
        _ => "other"
    };

    private sealed class ResponseShapeCollector(int responseBytes)
    {
        private string _rootType = "invalid";
        private readonly List<string> _topLevelFields = [];
        private int _topLevelFieldCount;
        private string _statusClass = "missing";
        private bool _hasOutputText;
        private readonly List<string> _outputBlockTypes = [];

        public void SetRoot(JsonElement root)
        {
            _rootType = root.ValueKind switch
            {
                JsonValueKind.Object => "object",
                JsonValueKind.Array => "array",
                JsonValueKind.String => "string",
                JsonValueKind.Number => "number",
                JsonValueKind.True or JsonValueKind.False => "boolean",
                JsonValueKind.Null => "null",
                _ => "other"
            };

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in root.EnumerateObject())
                {
                    _topLevelFieldCount++;
                    if (_topLevelFields.Count >= 64)
                    {
                        continue;
                    }

                    _topLevelFields.Add(SanitizeName(property.Name));
                }
            }
        }

        public void SetStatus(string statusClass) => _statusClass = statusClass;

        public void SetOutputShape(JsonElement root)
        {
            _hasOutputText = root.TryGetProperty("output_text", out _);
            if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var item in output.EnumerateArray())
            {
                if (_outputBlockTypes.Count >= 64)
                {
                    break;
                }

                if (item.ValueKind != JsonValueKind.Object)
                {
                    _outputBlockTypes.Add("other");
                    continue;
                }

                if (item.TryGetProperty("type", out var itemType) && itemType.ValueKind == JsonValueKind.String)
                {
                    _outputBlockTypes.Add(SanitizeName(itemType.GetString() ?? "other"));
                }

                if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var part in content.EnumerateArray())
                {
                    if (_outputBlockTypes.Count >= 64)
                    {
                        break;
                    }

                    if (part.ValueKind == JsonValueKind.Object
                        && part.TryGetProperty("type", out var partType)
                        && partType.ValueKind == JsonValueKind.String)
                    {
                        _outputBlockTypes.Add(SanitizeName(partType.GetString() ?? "other"));
                    }
                }
            }
        }

        public AiResponseShapeDiagnostic Build(
            string failureStage,
            string? firstSchemaFailurePath = null,
            string? firstSchemaFailureKeyword = null) => new(
            responseBytes,
            _rootType,
            _topLevelFieldCount,
            _topLevelFields.ToArray(),
            _statusClass,
            _hasOutputText,
            _outputBlockTypes.ToArray(),
            failureStage,
            firstSchemaFailurePath,
            firstSchemaFailureKeyword);

        private static string SanitizeName(string value)
        {
            if (value.Length is 0 or > 64
                || !value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'))
            {
                return "other";
            }

            return value;
        }
    }

    private static AiProviderUsage? ParseProviderUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var inputTokens = ReadNonNegativeInt(usage, "input_tokens");
        var outputTokens = ReadNonNegativeInt(usage, "output_tokens");
        var totalTokens = ReadNonNegativeInt(usage, "total_tokens");
        if (totalTokens == 0)
        {
            totalTokens = inputTokens + outputTokens;
        }

        var cacheHit = usage.TryGetProperty("cached_tokens", out var cached)
            && cached.ValueKind == JsonValueKind.Number
            && cached.GetInt32() > 0;
        return new AiProviderUsage(inputTokens, outputTokens, totalTokens, cacheHit);
    }

    private static AiProviderUsage? ParseResultUsage(JsonObject resultObject)
    {
        if (resultObject["usage"] is not JsonObject resultUsage)
        {
            return null;
        }

        var input = resultUsage["input_units"]?.GetValue<int>() ?? 0;
        var output = resultUsage["output_units"]?.GetValue<int>() ?? 0;
        return new AiProviderUsage(input, output, input + output, false);
    }

    private static int ReadNonNegativeInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
            && parsed >= 0
            ? parsed
            : 0;

    private static string? ExtractOutputText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var direct) && direct.ValueKind == JsonValueKind.String)
        {
            return direct.GetString();
        }

        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<string>? textSegments = null;
        foreach (var item in output.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!part.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (part.TryGetProperty("type", out var typeElement)
                    && (typeElement.ValueKind != JsonValueKind.String
                        || typeElement.GetString() is not ("output_text" or "text")))
                {
                    continue;
                }

                textSegments ??= [];
                textSegments.Add(text.GetString() ?? string.Empty);
            }
        }

        return textSegments is null ? null : string.Concat(textSegments);
    }

    private static AiAnalysisExecutionResult MapHttpFailure(HttpStatusCode statusCode, Stopwatch stopwatch) =>
        statusCode switch
        {
            HttpStatusCode.BadRequest => Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_BAD_REQUEST", false, "分析请求被供应商拒绝，请检查素材和合同后重试。", stopwatch),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_AUTH_FAILED", false, "AI 凭据无效或已撤回，请更新本机凭据后重试。", stopwatch),
            (HttpStatusCode)429 => Failure(AiProviderOutcome.RetryableError, "AI_PROVIDER_RATE_LIMITED", true, "供应商暂时限流，请稍后重试。", stopwatch),
            >= HttpStatusCode.InternalServerError and <= (HttpStatusCode)599 => Failure(AiProviderOutcome.RetryableError, "AI_PROVIDER_TEMPORARY_FAILURE", true, "供应商暂时不可用，请稍后重试。", stopwatch),
            _ => Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_HTTP_ERROR", false, "供应商请求失败，请检查连接和服务状态。", stopwatch)
        };

    private static AiAnalysisExecutionResult Failure(
        AiProviderOutcome outcome,
        string code,
        bool retryable,
        string userAction,
        Stopwatch stopwatch,
        AiProviderUsage? usage = null)
    {
        stopwatch.Stop();
        return AiAnalysisExecutionResult.Failure(
            outcome,
            new ContractError(code, retryable, "provider", userAction),
            stopwatch.Elapsed,
            usage);
    }

    private byte[] BuildProviderPayload(AiAnalysisRequestV1 request, List<AiAnalysisCopy> images)
    {
        var content = new JsonArray();
        for (var index = 0; index < request.Assets.Count; index++)
        {
            var copy = images[index];
            content.Add(new JsonObject
            {
                ["type"] = "input_text",
                ["text"] = JsonSerializer.Serialize(new
                {
                    image_alias = AiAnalysisResultReferenceNormalizer.AssetAlias(index)
                }, JsonOptions)
            });
            content.Add(new JsonObject
            {
                ["type"] = "input_image",
                ["image_url"] = "data:" + copy.MimeType + ";base64," + Convert.ToBase64String(copy.Content.Span)
            });
        }

        var requirementSummary = JsonSerializer.Serialize(
            request.Requirements.Select((requirement, index) => new
            {
                requirement_id = AiAnalysisResultReferenceNormalizer.RequirementAlias(index),
                selection_requirement = requirement.SelectionRequirement,
                layout_intent = requirement.LayoutIntent,
                required = requirement.Required
            }),
            JsonOptions);
        content.Add(new JsonObject
        {
            ["type"] = "input_text",
            ["text"] = "请仅返回符合 ai-analysis-result/v1 的 JSON 对象。每个 requirement_matches 元素只能包含 requirement_id、asset_id、rank、confidence、evidence_codes 五个字段，不要添加 matched 或其他字段。request_id="
                + AiAnalysisResultReferenceNormalizer.RequestAlias
                + "，model_config_version="
                + AiAnalysisResultReferenceNormalizer.ModelConfigVersionAlias
                + "，analysis_kind=" + request.AnalysisKind
                + "。analysis_kind=product_joint_understanding 时把全部图片视为同一目标款候选整组，按每个开放式 selection_requirement 独立排序；同一 asset_id 可以出现在多个图片框的排名中。每个 requirement_matches 的 evidence_codes 必须且只能包含一个匹配类别：精确符合用 exact，不影响用途的安全相近替代用 safe_similar，存在实质差异用 material_difference，错款、错变体、无关素材或不安全候选用 unsafe。不要根据文件名推断语义；没有安全候选时不得编造匹配。analysis_kind=selected_asset_geometry 时只分析请求中的已确认单图并返回其主体几何；analysis_kind=size_table_extraction 时只从清晰可读的尺码来源提取原文行列，缺失、低清或冲突时返回 warning 且 size_table_candidate=null，禁止猜值"
                + "。每个 image_alias 标签只对应紧随其后的那一张图片。结果中的 request_id、model_config_version、requirement_id、coverage_requirement_ids、missing_requirement_ids、asset_id、asset_ids 和 source_asset_id 必须原样使用本请求给出的短别名；不得猜测、改写或输出源系统真实标识"
                + "。除 requirement_matches 外，请把真实商品配色分成 variant_groups：只按商品本身颜色分组，不要按背景色、光线或后期色调分组；dimension 固定填 color，value 填你观察到的颜色名称。每组返回 coverage_requirement_ids、missing_requirement_ids、is_complete、evidence_codes；完整性按所需视角/姿态/部位是否覆盖判断，不能按照片数量判断。只有一个颜色完整时将其 is_primary_suggestion=true；多个颜色完整时只能建议一个主色并填写 primary_suggestion_evidence，后续生成会保持该主色一致；所有颜色都不完整时建议覆盖最多的一组并明确缺项。variant_role 用 primary 或 secondary 表达相对角色，不能把红、蓝、黄等示例颜色写死为产品规则。"
                + "。按以下品类中立图片框需求选择素材并给出证据，不要输出 Markdown 或隐藏推理："
                + requirementSummary
        });

        var root = new JsonObject
        {
            ["model"] = _options.ModelId,
            ["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = content
                }
            },
            ["thinking"] = new JsonObject { ["type"] = "disabled" },
            ["max_output_tokens"] = AiAnalysisProductionPolicy.MaxOutputTokensPerRequestV2,
            ["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = "ai_analysis_result",
                    ["strict"] = true,
                    ["schema"] = ResultSchema(request)
                }
            }
        };

        return Encoding.UTF8.GetBytes(root.ToJsonString(JsonOptions));
    }

    private static JsonObject ResultSchema(AiAnalysisRequestV1 request)
    {
        var requirementAliases = request.Requirements
            .Select((_, index) => AiAnalysisResultReferenceNormalizer.RequirementAlias(index))
            .ToArray();
        var assetAliases = request.Assets
            .Select((_, index) => AiAnalysisResultReferenceNormalizer.AssetAlias(index))
            .ToArray();

        return new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("schema_version", "document_kind", "request_id", "model_config_version", "requirement_matches", "variant_groups", "subject_geometry", "size_table_candidate", "warnings", "usage"),
            ["properties"] = new JsonObject
            {
                ["schema_version"] = new JsonObject { ["const"] = "ai-analysis-result/v1" },
                ["document_kind"] = new JsonObject { ["const"] = "result" },
                ["request_id"] = new JsonObject { ["const"] = AiAnalysisResultReferenceNormalizer.RequestAlias },
                ["provider_request_ref"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
                ["model_config_version"] = new JsonObject { ["const"] = AiAnalysisResultReferenceNormalizer.ModelConfigVersionAlias },
                ["requirement_matches"] = ArraySchema(MatchSchema(requirementAliases, assetAliases), 40000),
                ["variant_groups"] = ArraySchema(VariantSchema(assetAliases, requirementAliases), 200),
                ["subject_geometry"] = ArraySchema(GeometrySchema(assetAliases), 20000),
                ["size_table_candidate"] = new JsonObject { ["anyOf"] = new JsonArray(SizeTableSchema(assetAliases), new JsonObject { ["type"] = "null" }) },
                ["warnings"] = ArraySchema(WarningSchema(), 200),
                ["usage"] = UsageSchema()
            }
        };
    }

    private static JsonObject StringSchema(int minLength, int maxLength) => new()
    {
        ["type"] = "string",
        ["minLength"] = minLength,
        ["maxLength"] = maxLength
    };

    private static JsonObject IdSchema() => new()
    {
        ["type"] = "string",
        ["minLength"] = 1,
        ["maxLength"] = 128,
        ["pattern"] = "^[A-Za-z0-9_-]+$"
    };

    private static JsonObject ArraySchema(JsonObject itemSchema, int maxItems) => new()
    {
        ["type"] = "array",
        ["maxItems"] = maxItems,
        ["items"] = itemSchema
    };

    private static JsonObject MatchSchema(
        IReadOnlyList<string> requirementAliases,
        IReadOnlyList<string> assetAliases) =>
        new()
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("requirement_id", "asset_id", "rank", "confidence", "evidence_codes"),
            ["properties"] = new JsonObject
            {
                ["requirement_id"] = AllowedReferenceSchema(requirementAliases),
                ["asset_id"] = AllowedReferenceSchema(assetAliases),
                ["rank"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 20000 },
                ["confidence"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 },
                ["evidence_codes"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 32, ["uniqueItems"] = true, ["items"] = IdSchema() }
            }
        };

    private static JsonObject VariantSchema(
        IReadOnlyList<string> assetAliases,
        IReadOnlyList<string>? requirementAliases = null) => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("variant_id", "dimension", "value", "asset_ids", "is_primary_suggestion"),
        ["properties"] = new JsonObject
        {
            ["variant_id"] = IdSchema(),
            ["dimension"] = StringSchema(1, 200),
            ["value"] = StringSchema(1, 200),
            ["asset_ids"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 20000, ["uniqueItems"] = true, ["items"] = AllowedReferenceSchema(assetAliases) },
            ["is_primary_suggestion"] = new JsonObject { ["type"] = "boolean" },
            ["variant_role"] = new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("primary", "secondary") }, new JsonObject { ["type"] = "null" }) },
            ["coverage_requirement_ids"] = new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "array", ["uniqueItems"] = true, ["maxItems"] = 20000, ["items"] = AllowedReferenceSchema(requirementAliases ?? []) }, new JsonObject { ["type"] = "null" }) },
            ["missing_requirement_ids"] = new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "array", ["uniqueItems"] = true, ["maxItems"] = 20000, ["items"] = AllowedReferenceSchema(requirementAliases ?? []) }, new JsonObject { ["type"] = "null" }) },
            ["is_complete"] = new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "boolean" }, new JsonObject { ["type"] = "null" }) },
            ["evidence_codes"] = new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 32, ["uniqueItems"] = true, ["items"] = IdSchema() }, new JsonObject { ["type"] = "null" }) },
            ["primary_suggestion_evidence"] = new JsonObject { ["anyOf"] = new JsonArray(StringSchema(1, 500), new JsonObject { ["type"] = "null" }) }
        }
    };

    private static JsonObject GeometrySchema(IReadOnlyList<string> assetAliases) => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("asset_id", "left", "top", "right", "bottom", "confidence"),
        ["properties"] = new JsonObject
        {
            ["asset_id"] = AllowedReferenceSchema(assetAliases),
            ["left"] = UnitIntervalSchema(),
            ["top"] = UnitIntervalSchema(),
            ["right"] = UnitIntervalSchema(),
            ["bottom"] = UnitIntervalSchema(),
            ["confidence"] = UnitIntervalSchema()
        }
    };

    private static JsonObject UnitIntervalSchema() => new() { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 };

    private static JsonObject SizeTableSchema(IReadOnlyList<string> assetAliases) => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("source_asset_id", "confidence", "columns", "rows"),
        ["properties"] = new JsonObject
        {
            ["source_asset_id"] = AllowedReferenceSchema(assetAliases),
            ["confidence"] = UnitIntervalSchema(),
            ["columns"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 50, ["items"] = StringSchema(1, 200) },
            ["rows"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 200, ["items"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 50, ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = 500 } } }
        }
    };

    private static JsonObject WarningSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("code", "scope", "message_summary"),
        ["properties"] = new JsonObject
        {
            ["code"] = IdSchema(),
            ["scope"] = new JsonObject { ["enum"] = new JsonArray("request", "product", "requirement", "asset", "size_table") },
            ["message_summary"] = StringSchema(1, 500)
        }
    };

    private static JsonObject UsageSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("usage_event_id", "input_units", "output_units", "request_count"),
        ["properties"] = new JsonObject
        {
            ["usage_event_id"] = IdSchema(),
            ["input_units"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
            ["output_units"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
            ["request_count"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
            ["provider_cost_amount"] = new JsonObject { ["type"] = new JsonArray("number", "null"), ["minimum"] = 0 },
            ["provider_cost_currency"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["pattern"] = "^[A-Z]{3}$" }
        }
    };

    private static JsonObject AllowedReferenceSchema(IEnumerable<string> aliases)
    {
        var values = new JsonArray();
        foreach (var alias in aliases)
        {
            values.Add(alias);
        }

        return new JsonObject
        {
            ["type"] = "string",
            ["enum"] = values
        };
    }

    private byte[] BuildReferenceLearningPayload(
        ReferenceLearningAiRequestV2 request,
        List<AiAnalysisCopy> images)
    {
        var providerFrames = request.Frames.Select((frame, index) => new
        {
            frame_id = $"f_{index + 1:D2}",
            page_id = frame.PageId,
            reference_asset_id = frame.ReferenceAssetId,
            page_context = frame.PageContext,
            frame_context = frame.FrameContext,
            layout_intent = frame.LayoutIntent
        }).ToArray();
        var content = new JsonArray();
        for (var index = 0; index < request.Assets.Count; index++)
        {
            // Keep the stable asset identity immediately adjacent to the
            // image it names. A trailing alias table after a run of images
            // lets a multimodal model shift one photo's semantics onto the
            // next frame even though the local frame/asset references close.
            content.Add(new JsonObject
            {
                ["type"] = "input_text",
                ["text"] = $"REFERENCE_ASSET asset_id={request.Assets[index].AssetId} frame_ids="
                    + string.Join(",", providerFrames
                        .Where(frame => string.Equals(
                            frame.reference_asset_id,
                            request.Assets[index].AssetId,
                            StringComparison.Ordinal))
                        .Select(frame => frame.frame_id))
            });
            content.Add(new JsonObject
            {
                ["type"] = "input_image",
                ["image_url"] = "data:" + images[index].MimeType + ";base64," + Convert.ToBase64String(images[index].Content.Span)
            });
        }

        var framePrompts = JsonSerializer.Serialize(providerFrames, JsonOptions);
        const string instruction = "每个 REFERENCE_ASSET 文本后紧邻的下一张图片就是该 asset_id，frame_ids 是只允许读取这张图片的框，不得将不同照片的内容互换。"
            + "结合每框已匹配的参考图，为未来不同款商品生成可复用的图片类别短语。selection_requirement 只保留未来候选图可重复判断的对象或商品部位、朝向、姿态、景别等必要区分，例如“人物正面端坐图”“人物背面全身图”“领口与尺码标细节图”“袖口工艺特写图”“面料纹理特写图”“成分信息图”；示例不是固定枚举。"
            + "禁止复述单张参考照片，禁止写衣服或商品颜色、背景、墙面、地面、家具、道具、饰品、光线、色调、人物身份等偶然属性。selection_requirement 必须是 2至40 个字符的单一类别短语，不得包含逗号、句号、分号、冒号或换行；判断理由只写入 positive_evidence 和 exclusion_evidence。"
            + "frame_context 和 layout_intent 仅是本地结构审计元数据，不能被改写进 selection_requirement；不能单独根据文件名、图层名、尺寸、位置或版式臆测语义。rankings 必须为空。必须为每个给定 frame_id 返回恰好一个 frame_results 元素，并逐字复制给定的短 frame_id（例如 f_01），不得改写、翻译、合并或新增 ID。";
        content.Add(new JsonObject
        {
            ["type"] = "input_text",
            ["text"] = "只返回符合 reference-learning-ai-result/v2 的 JSON，不输出 Markdown 或隐藏推理。operation="
                + request.Operation + "，request_id=" + request.RequestId
                + "，model_config_version=" + request.ModelConfigVersion + "。" + instruction
                + "图片框上下文：" + framePrompts
        });

        var root = new JsonObject
        {
            ["model"] = _options.ModelId,
            ["input"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = content
            }),
            ["thinking"] = new JsonObject { ["type"] = "disabled" },
            ["max_output_tokens"] = 4096,
            ["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = "reference_learning_ai_result_v2",
                    ["strict"] = true,
                    ["schema"] = ReferenceLearningResultSchema(request.Operation)
                }
            }
        };
        return Encoding.UTF8.GetBytes(root.ToJsonString(JsonOptions));
    }

    private ReferenceLearningAiExecutionResult ParseReferenceLearningResponse(
        ReferenceLearningAiRequestV2 request,
        byte[] body,
        Stopwatch stopwatch)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(body); }
        catch (JsonException)
        {
            return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商返回的数据无法校验。", stopwatch);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商返回的数据无法校验。", stopwatch);
            }

            var status = root.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String
                ? statusElement.GetString()
                : null;
            var usage = ParseProviderUsage(root);
            if (status is "in_progress" or "queued" or "processing")
            {
                return ReferenceFailure(AiProviderOutcome.AiResultUnknown, "AI_PROVIDER_RESULT_PENDING", false, "供应商仍在处理，结果未知。", stopwatch, usage);
            }
            if (string.Equals(status, "incomplete", StringComparison.Ordinal))
            {
                return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_OUTPUT_INCOMPLETE", false, "供应商未完整生成语义结果，当前响应不能使用；请人工检查后再决定是否建立新请求。", stopwatch, usage);
            }
            if (string.Equals(status, "failed", StringComparison.Ordinal))
            {
                return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_REQUEST_FAILED", false, "供应商明确返回语义请求失败；请检查任务后再决定是否建立新请求。", stopwatch, usage);
            }
            if (status is "cancelled" or "canceled")
            {
                return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_REQUEST_CANCELLED", false, "供应商明确返回语义请求已取消；请检查任务后再决定是否建立新请求。", stopwatch, usage);
            }
            if (status is not ("completed" or "complete"))
            {
                return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_STATUS_INVALID", false, "供应商返回了无法确认的语义请求状态，已安全拒绝。", stopwatch, usage);
            }
            var outputText = ExtractOutputText(root);
            if (string.IsNullOrWhiteSpace(outputText))
            {
                return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商未返回结构化语义结果。", stopwatch, usage);
            }

            JsonObject resultObject;
            try { resultObject = JsonNode.Parse(outputText)?.AsObject() ?? throw new JsonException(); }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商语义结果不是有效 JSON。", stopwatch, usage);
            }

            usage ??= ParseResultUsage(resultObject);
            if (!resultObject.ContainsKey("usage"))
            {
                resultObject["usage"] = new JsonObject
                {
                    ["usage_event_id"] = "usage_" + request.RequestId,
                    ["input_units"] = usage?.InputTokens ?? 0,
                    ["output_units"] = usage?.OutputTokens ?? 0,
                    ["request_count"] = 1,
                    ["provider_cost_amount"] = null,
                    ["provider_cost_currency"] = null
                };
            }
            var serialized = resultObject.ToJsonString(JsonOptions);
            if (!_contractValidator.Validate(ContractDocumentKind.ReferenceLearningAi, serialized).IsValid)
            {
                return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商语义结果不符合 v2 合同。", stopwatch, usage);
            }

            ReferenceLearningAiResultV2 result;
            try { result = JsonSerializer.Deserialize<ReferenceLearningAiResultV2>(serialized, JsonOptions) ?? throw new JsonException(); }
            catch (JsonException)
            {
                return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商语义结果无法读取。", stopwatch, usage);
            }
            result = result with
            {
                FrameResults = result.FrameResults
                    .Select(frame => TryMapReferenceFrameAlias(request, frame))
                    .ToArray()
            };
            if (!string.Equals(result.RequestId, request.RequestId, StringComparison.Ordinal)
                || !string.Equals(result.Operation, request.Operation, StringComparison.Ordinal)
                || !string.Equals(result.ModelConfigVersion, request.ModelConfigVersion, StringComparison.Ordinal)
                || result.FrameResults.Select(item => item.FrameId).Distinct(StringComparer.Ordinal).Count() != result.FrameResults.Count
                || result.FrameResults.Any(item => request.Frames.All(frame => !string.Equals(frame.FrameId, item.FrameId, StringComparison.Ordinal))))
            {
                return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_REFERENCE_MISMATCH", false, "供应商语义结果引用了当前请求之外的数据。", stopwatch, usage);
            }
            if (request.Operation == ReferenceLearningAiOperation.GenerateSelectionRequirements
                && result.FrameResults.Count != request.Frames.Count)
            {
                return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "语义生成结果缺少图片框或包含不允许的候选排名。", stopwatch, usage);
            }
            stopwatch.Stop();
            return ReferenceLearningAiExecutionResult.Completed(result, usage, stopwatch.Elapsed);
        }
    }

    private static ReferenceLearningFrameResultV2 TryMapReferenceFrameAlias(
        ReferenceLearningAiRequestV2 request,
        ReferenceLearningFrameResultV2 frame)
    {
        if (request.Frames.Any(candidate =>
                string.Equals(candidate.FrameId, frame.FrameId, StringComparison.Ordinal)))
        {
            return frame;
        }

        if (!frame.FrameId.StartsWith("f_", StringComparison.Ordinal)
            || !int.TryParse(frame.FrameId.AsSpan(2), out var ordinal)
            || ordinal < 1
            || ordinal > request.Frames.Count)
        {
            return frame;
        }

        // The provider sees short deterministic aliases because long random
        // frame IDs are easy for a vision model to mistype in a large result.
        // Mapping is positional and remains closed over this request; unknown,
        // duplicate, missing, or out-of-range IDs still fail the checks below.
        return frame with { FrameId = request.Frames[ordinal - 1].FrameId };
    }

    private static void ValidateReferenceCopy(
        ReferenceLearningAiRequestV2 request,
        AiAssetInputV1 asset,
        AiAnalysisCopy copy)
    {
        if (copy.Content.IsEmpty
            || !string.Equals(copy.Reference, asset.AnalysisCopyReference, StringComparison.Ordinal)
            || !IsSupportedMime(copy.MimeType)
            || copy.PixelWidth <= 0 || copy.PixelHeight <= 0)
            throw new InvalidDataException("AI_ANALYSIS_COPY_METADATA_INVALID");
        var digest = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(copy.Content.Span)).ToLowerInvariant();
        if (!string.Equals(copy.ContentDigest, digest, StringComparison.Ordinal)
            || copy.ByteLength != copy.Content.Length
            || !string.Equals(asset.AnalysisCopyDigest, digest, StringComparison.Ordinal)
            || asset.AnalysisCopyLength != copy.Content.Length
            || !string.Equals(asset.AnalysisCopyMimeType, copy.MimeType, StringComparison.Ordinal)
            || !string.Equals(asset.AnalysisCopyLeaseId, copy.LeaseId, StringComparison.Ordinal)
            || !string.Equals(asset.AnalysisCopyCompressionProfile, request.CompressionProfileVersion, StringComparison.Ordinal)
            || !string.Equals(copy.CompressionProfileVersion, request.CompressionProfileVersion, StringComparison.Ordinal))
            throw new InvalidDataException("AI_ANALYSIS_COPY_IDENTITY_MISMATCH");
    }

    private static JsonObject ReferenceLearningResultSchema(string operation) => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("schema_version", "document_kind", "operation", "request_id", "model_config_version", "frame_results", "warnings", "usage"),
        ["properties"] = new JsonObject
        {
            ["schema_version"] = new JsonObject { ["const"] = ReferenceLearningAiProtocol.ResultSchemaVersion },
            ["document_kind"] = new JsonObject { ["const"] = "result" },
            ["operation"] = new JsonObject { ["const"] = operation },
            ["request_id"] = IdSchema(),
            ["model_config_version"] = StringSchema(1, 128),
            ["frame_results"] = ArraySchema(ReferenceFrameResultSchema(), 200),
            ["warnings"] = ArraySchema(WarningSchema(), 200),
            ["usage"] = UsageSchema()
        }
    };

    private static JsonObject ReferenceFrameResultSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("frame_id", "selection_requirement", "positive_evidence", "exclusion_evidence", "confidence"),
        ["properties"] = new JsonObject
        {
            ["frame_id"] = IdSchema(),
            ["selection_requirement"] = ReferenceSelectionRequirementSchema(),
            ["positive_evidence"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 8, ["uniqueItems"] = true, ["items"] = StringSchema(1, 200) },
            ["exclusion_evidence"] = new JsonObject { ["type"] = "array", ["maxItems"] = 8, ["uniqueItems"] = true, ["items"] = StringSchema(1, 200) },
            ["confidence"] = UnitIntervalSchema()
        }
    };

    private static JsonObject ReferenceSelectionRequirementSchema() => new()
    {
        ["type"] = "string",
        ["description"] = "未来不同商品可复用的图片类别短语，只写对象或部位、朝向、姿态、景别等必要区分，不得复述参考照片的颜色、背景、家具、道具、饰品、光线或其他偶然细节。",
        ["minLength"] = ReferenceSelectionRequirementPolicy.MinLength,
        ["maxLength"] = ReferenceSelectionRequirementPolicy.MaxLength,
        ["pattern"] = "^[^,，.。;；:：\\r\\n]+$"
    };

    private static void ValidateReferenceLearningRequest(ReferenceLearningAiRequestV2 request, int maxAssets)
    {
        if (!string.Equals(request.SchemaVersion, ReferenceLearningAiProtocol.RequestSchemaVersion, StringComparison.Ordinal)
            || !string.Equals(request.DocumentKind, "request", StringComparison.Ordinal)
            || !ReferenceLearningAiOperation.IsSupported(request.Operation)
            || !string.Equals(
                request.PromptContractVersion,
                ReferenceLearningAiProtocol.SemanticPromptVersion,
                StringComparison.Ordinal)
            || !string.Equals(request.ResponseSchemaVersion, ReferenceLearningAiProtocol.ResultSchemaVersion, StringComparison.Ordinal)
            || !IsApprovedCompressionProfile(request.CompressionProfileVersion)
            || request.Assets.Count is 0 || request.Assets.Count > maxAssets
            || request.Frames.Count is 0 or > 200
            || request.Assets.Select(asset => asset.AssetId).Distinct(StringComparer.Ordinal).Count() != request.Assets.Count
            || request.Frames.Select(frame => frame.FrameId).Distinct(StringComparer.Ordinal).Count() != request.Frames.Count
            || request.Assets.Any(asset => asset.AnalysisCopyDigest is null
                || asset.AnalysisCopyLength is null or <= 0
                || asset.AnalysisCopyMimeType is null
                || asset.AnalysisCopyCompressionProfile is null
                || asset.AnalysisCopyLeaseId is null)
            || request.Assets.Any(asset => request.Frames.All(frame =>
                !string.Equals(frame.ReferenceAssetId, asset.AssetId, StringComparison.Ordinal)))
            || request.Frames.Any(frame => string.IsNullOrWhiteSpace(frame.ReferenceAssetId)
                || request.Assets.All(asset => !string.Equals(asset.AssetId, frame.ReferenceAssetId, StringComparison.Ordinal))))
        {
            throw new InvalidOperationException("TPL_LEARN_AI_REQUEST_INVALID");
        }
    }

    private static ReferenceLearningAiExecutionResult MapReferenceHttpFailure(HttpStatusCode statusCode, Stopwatch stopwatch) => statusCode switch
    {
        HttpStatusCode.BadRequest => ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_BAD_REQUEST", false, "语义分析请求被供应商拒绝。", stopwatch),
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_AUTH_FAILED", false, "AI 凭据无效或已撤回。", stopwatch),
        (HttpStatusCode)429 => ReferenceFailure(AiProviderOutcome.RetryableError, "AI_PROVIDER_RATE_LIMITED", true, "供应商暂时限流，请稍后重试。", stopwatch),
        >= HttpStatusCode.InternalServerError and <= (HttpStatusCode)599 => ReferenceFailure(AiProviderOutcome.RetryableError, "AI_PROVIDER_TEMPORARY_FAILURE", true, "供应商暂时不可用，请稍后重试。", stopwatch),
        _ => ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_HTTP_ERROR", false, "供应商请求失败，请检查连接和服务状态。", stopwatch)
    };

    private static ReferenceLearningAiExecutionResult ReferenceFailure(
        AiProviderOutcome outcome,
        string code,
        bool retryable,
        string userAction,
        Stopwatch stopwatch,
        AiProviderUsage? usage = null)
    {
        stopwatch.Stop();
        return ReferenceLearningAiExecutionResult.Failure(
            outcome,
            new ContractError(code, retryable, "provider", userAction),
            stopwatch.Elapsed,
            usage);
    }

    private string RequestTooLargeUserAction(int actualBytes) =>
        $"最终 UTF-8 JSON 请求体为 {actualBytes:N0} bytes，超过应用本地传输预算 {_options.MaxRequestBytes:N0} bytes（当前为 16 MiB，不是火山 API 硬限制）；本次未发送 AI 请求。请减少本款照片后重新建立批次。";

    private static string? ReadSafeProviderReference(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = id.GetString();
        return value is not null
            && value.Length <= 256
            && value.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or ':' or '-')
            ? value
            : null;
    }

    private static void ValidateOptions(VolcArkAiOptions options)
    {
        if (!string.Equals(options.ModelId, "doubao-seed-2-1-pro-260628", StringComparison.Ordinal)
            || !Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps
            || options.EffectiveRequestTimeout <= TimeSpan.Zero
            || options.EffectiveRequestTimeout > TimeSpan.FromSeconds(300)
            || options.EffectiveUploadTimeout <= TimeSpan.Zero
            || options.EffectiveUploadTimeout > TimeSpan.FromSeconds(240)
            || options.EffectiveUploadIdleTimeout <= TimeSpan.Zero
            || options.EffectiveUploadIdleTimeout > TimeSpan.FromSeconds(90)
            || options.EffectiveResponseHeadersTimeout <= TimeSpan.Zero
            || options.EffectiveResponseHeadersTimeout > TimeSpan.FromSeconds(120)
            || options.EffectiveResponseBodyTimeout <= TimeSpan.Zero
            || options.EffectiveResponseBodyTimeout > TimeSpan.FromSeconds(60)
            || options.MaxRequestBytes is < 1024 or > 16 * 1024 * 1024
            || options.MaxResponseBytes is < 1024 or > 16 * 1024 * 1024
            || options.MaxAssetsPerRequest is < 1 or > AiAnalysisProductionPolicy.MaxTargetAssetsPerRequest
            || options.MaxReferenceAssetsPerRequest is < 1 or > AiAnalysisProductionPolicy.MaxReferenceAssetsPerRequest)
        {
            throw new ArgumentException("火山方舟配置不符合已批准的安全边界。", nameof(options));
        }
    }

    private static void ValidateRequest(AiAnalysisRequestV1 request, int maxAssetsPerRequest)
    {
        if (!string.Equals(request.SchemaVersion, AiAnalysisProtocol.RequestSchemaVersion, StringComparison.Ordinal)
            || !string.Equals(request.DocumentKind, "request", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(request.RequestId)
            || request.Assets.Count == 0
            || request.Assets.Count > maxAssetsPerRequest
            || !IsApprovedCompressionProfile(request.CompressionProfileVersion)
            || request.Assets.Any(asset =>
                asset.AnalysisCopyDigest is null
                || asset.AnalysisCopyLength is null or <= 0
                || asset.AnalysisCopyMimeType is null
                || !IsSupportedMime(asset.AnalysisCopyMimeType)
                || asset.AnalysisCopyCompressionProfile is null
                || !string.Equals(
                    request.CompressionProfileVersion,
                    asset.AnalysisCopyCompressionProfile,
                    StringComparison.Ordinal))
            || request.Requirements.Count == 0)
        {
            throw new InvalidOperationException("AI_ANALYSIS_REQUEST_INVALID");
        }
    }

    private static bool IsApprovedCompressionProfile(string value) =>
        AiAnalysisCopyProfiles.IsApproved(value);

    private static void ConfigureTransportRequest(HttpRequestMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Use the conventional HTTP/1.1 request shape, but leave the policy
        // negotiable/fallback-capable.  An exact-version pin adds an avoidable
        // connection negotiation delay on this endpoint and is not required by
        // the provider contract.  The explicit header prevents a delayed
        // 100-continue handshake from consuming the upload budget.
        message.Version = HttpVersion.Version11;
        message.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        message.Headers.ExpectContinue = false;
    }
}
