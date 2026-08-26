namespace EverTask.Monitor.Api.DTOs.Auth;

/// <summary>
/// Login response containing JWT token and expiration information.
/// </summary>
/// <param name="Token">The JWT Bearer token to use for subsequent authenticated requests.</param>
/// <param name="ExpiresAt">The UTC timestamp when the token expires.</param>
/// <param name="Username">The username associated with this token.</param>
public record LoginResponse(
    string Token,
    DateTimeOffset ExpiresAt,
    string Username
)
{
    /// <summary>
    /// Whether this session carries the operate role, i.e. whether the management endpoints will accept it.
    /// False for the dashboard credential and for every magic link.
    /// </summary>
    public bool CanManage { get; init; }
}
