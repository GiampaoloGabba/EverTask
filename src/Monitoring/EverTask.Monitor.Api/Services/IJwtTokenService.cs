using EverTask.Monitor.Api.DTOs.Auth;

namespace EverTask.Monitor.Api.Services;

/// <summary>
/// Service for generating and validating JWT tokens.
/// </summary>
public interface IJwtTokenService
{
    /// <summary>
    /// Generate a JWT token for the specified username.
    /// </summary>
    /// <param name="username">The username to generate the token for.</param>
    /// <returns>The generated JWT token and expiration information.</returns>
    LoginResponse GenerateToken(string username) => GenerateToken(username, false);

    /// <summary>
    /// Generate a JWT token for the specified username and management capability.
    /// </summary>
    /// <param name="username">The username to generate the token for.</param>
    /// <param name="canManage">
    /// Whether the session may call the management endpoints. False issues a read-only token, which is what
    /// the dashboard credential and every magic link get.
    /// </param>
    /// <returns>The generated JWT token and expiration information.</returns>
    LoginResponse GenerateToken(string username, bool canManage);

    /// <summary>
    /// Validate a JWT token and extract claims.
    /// </summary>
    /// <param name="token">The JWT token to validate.</param>
    /// <returns>Validation result with token information.</returns>
    TokenValidationResponse ValidateToken(string token);
}
