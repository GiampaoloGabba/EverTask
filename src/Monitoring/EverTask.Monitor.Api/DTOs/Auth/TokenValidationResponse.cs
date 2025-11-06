namespace EverTask.Monitor.Api.DTOs.Auth;

/// <summary>
/// Token validation response containing validation result and token information.
/// </summary>
public record TokenValidationResponse(
    bool IsValid,
    string? Username,
    DateTimeOffset? ExpiresAt
);
