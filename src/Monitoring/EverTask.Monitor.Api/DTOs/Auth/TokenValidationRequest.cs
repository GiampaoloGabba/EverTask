namespace EverTask.Monitor.Api.DTOs.Auth;

/// <summary>
/// Request model for token validation endpoint.
/// </summary>
/// <param name="Token">JWT token to validate</param>
public record TokenValidationRequest(string? Token);
