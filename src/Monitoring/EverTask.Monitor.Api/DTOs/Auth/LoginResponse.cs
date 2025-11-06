namespace EverTask.Monitor.Api.DTOs.Auth;

/// <summary>
/// Login response containing JWT token and expiration information.
/// </summary>
public record LoginResponse(
    string Token,
    DateTimeOffset ExpiresAt,
    string Username
);
