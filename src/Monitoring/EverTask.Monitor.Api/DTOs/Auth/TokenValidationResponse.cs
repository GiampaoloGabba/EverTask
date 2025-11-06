namespace EverTask.Monitor.Api.DTOs.Auth;

/// <summary>
/// Token validation response containing validation result and token information.
/// </summary>
/// <param name="IsValid">Indicates whether the token is valid.</param>
/// <param name="Username">The username associated with the token, if valid.</param>
/// <param name="ExpiresAt">The UTC timestamp when the token expires, if valid.</param>
public record TokenValidationResponse(
    bool IsValid,
    string? Username,
    DateTimeOffset? ExpiresAt
);
