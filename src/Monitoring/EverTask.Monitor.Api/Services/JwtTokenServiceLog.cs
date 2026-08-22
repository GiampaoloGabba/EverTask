using Microsoft.Extensions.Logging;

namespace EverTask.Monitor.Api.Services;

// EventId range 3100-3199 (JwtTokenService). Ranges are allocated per component in the #32 plan;
// a reflection test asserts solution-wide uniqueness.
internal static partial class JwtTokenServiceLog
{
    [LoggerMessage(EventId = 3100, Level = LogLevel.Warning,
        Message = "JWT secret not configured. Generated random secret. " +
                  "This is NOT recommended for production or multi-instance deployments. " +
                  "Configure JwtSecret in EverTaskApiOptions")]
    public static partial void JwtSecretNotConfigured(this ILogger logger);

    [LoggerMessage(EventId = 3101, Level = LogLevel.Warning,
        Message = "JWT secret is shorter than recommended minimum (32 bytes / 256 bits). " +
                  "Consider using a stronger secret for production environments")]
    public static partial void JwtSecretTooShort(this ILogger logger);

    [LoggerMessage(EventId = 3102, Level = LogLevel.Information,
        Message = "Generated JWT token for user '{Username}' (expires: {ExpiresAt})")]
    public static partial void JwtTokenGenerated(this ILogger logger, string username, DateTimeOffset expiresAt);

    [LoggerMessage(EventId = 3103, Level = LogLevel.Debug,
        Message = "JWT token validated successfully for user '{Username}'")]
    public static partial void JwtTokenValidated(this ILogger logger, string? username);

    [LoggerMessage(EventId = 3104, Level = LogLevel.Debug,
        Message = "JWT token expired: {Reason}")]
    public static partial void JwtTokenExpired(this ILogger logger, string reason);

    [LoggerMessage(EventId = 3105, Level = LogLevel.Debug,
        Message = "JWT token validation failed: {Reason}")]
    public static partial void JwtTokenValidationFailed(this ILogger logger, string reason);

    [LoggerMessage(EventId = 3106, Level = LogLevel.Warning,
        Message = "Unexpected error during JWT token validation")]
    public static partial void JwtTokenValidationError(this ILogger logger, Exception exception);
}
