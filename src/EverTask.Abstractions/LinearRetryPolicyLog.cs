using Microsoft.Extensions.Logging;

namespace EverTask.Resilience;

// EventId range 1900-1999 (LinearRetryPolicy). Ranges are allocated per component in the #32 plan;
// a reflection test asserts solution-wide uniqueness.
// SkipEnabledCheck on the two {ExceptionType} methods: the call sites evaluate ex.GetType().Name, so they
// are already wrapped in an explicit IsEnabled guard (the generator's own guard runs after the arguments).
internal static partial class LinearRetryPolicyLog
{
    [LoggerMessage(EventId = 1900, Level = LogLevel.Warning, SkipEnabledCheck = true,
        Message = "Exception {ExceptionType} is not retryable, failing immediately")]
    public static partial void ExceptionNotRetryable(this ILogger logger, Exception exception, string exceptionType);

    [LoggerMessage(EventId = 1901, Level = LogLevel.Warning, SkipEnabledCheck = true,
        Message = "Retry attempt {Attempt} of {MaxRetries} after {DelayMs}ms for {ExceptionType}")]
    public static partial void RetryAttempt(this ILogger logger, Exception exception, int attempt, int maxRetries,
                                            double delayMs, string exceptionType);

    [LoggerMessage(EventId = 1902, Level = LogLevel.Error,
        Message = "OnRetry callback failed for attempt {Attempt}, continuing with retry")]
    public static partial void OnRetryCallbackFailed(this ILogger logger, Exception exception, int attempt);
}
