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
public sealed partial class VolcArkAiAdapter : IAiServicePort
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
                        return ReferenceFailure(AiProviderOutcome.AiResultUnknown, "AI_REQUEST_CANCELLED_AFTER_SUBMISSION", false, "已停止等待；供应商结果未确定，禁止自动重发。", stopwatch);
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
                        return ReferenceFailure(AiProviderOutcome.AiResultUnknown, "AI_REQUEST_CANCELLED_AFTER_SUBMISSION", false, "已停止等待；供应商结果未确定，禁止自动重发。", stopwatch);
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
                        return ReferenceFailure(AiProviderOutcome.AiResultUnknown, "AI_REQUEST_CANCELLED_AFTER_SUBMISSION", false, "已停止等待；供应商结果未确定，禁止自动重发。", stopwatch);
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


}
