namespace Desktop.Application.Ai;

/// <summary>
/// Centralized policy for durable AI ledger status strings. Persistence keeps the
/// strings for compatibility; workflow code should use this policy for meaning.
/// </summary>
public static class AiLedgerStatusPolicy
{
    public const string Queued = "Queued";
    public const string Uploading = "Uploading";
    public const string Analysing = "Analysing";
    public const string RetryableError = "RetryableError";
    public const string AiResultUnknown = "AiResultUnknown";
    public const string TerminalError = "TerminalError";
    public const string Cancelled = "Cancelled";
    public const string Validated = "Validated";
    public const string ManualReview = "ManualReview";
    public const string PlanClosed = "PlanClosed";

    public static bool IsRunnable(string? status) => status is Queued or RetryableError;

    public static bool IsInFlight(string? status) => status is Uploading or Analysing;

    public static bool IsUnknown(string? status) => status == AiResultUnknown;

    public static bool IsTerminal(string? status) => status is Validated or ManualReview or PlanClosed
        or TerminalError or AiResultUnknown or Cancelled;

    public static bool IsTerminalFailure(string? status) => status is TerminalError or AiResultUnknown;

    public static bool IsRecoverableAdmissionState(string? status) => status is Queued or RetryableError
        or Uploading or Analysing;
}
