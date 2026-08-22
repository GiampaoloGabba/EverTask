namespace EverTask.Monitor.Api.DTOs.Auth;

/// <summary>
/// Request model for the body-based magic link exchange endpoint.
/// </summary>
/// <param name="Token">The magic link token</param>
public record MagicLinkLoginRequest(string? Token);
