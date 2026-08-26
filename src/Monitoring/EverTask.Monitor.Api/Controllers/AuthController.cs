using System.Security.Cryptography;
using System.Text;
using EverTask.Monitor.Api.DTOs.Auth;
using EverTask.Monitor.Api.Options;
using EverTask.Monitor.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace EverTask.Monitor.Api.Controllers;

/// <summary>
/// Handles authentication and JWT token operations.
/// </summary>
[ApiController]
[Route("api/auth")]
[AllowAnonymous] // Auth endpoints must be accessible without authentication
public class AuthController : ControllerBase
{
    private readonly IJwtTokenService _jwtTokenService;
    private readonly EverTaskApiOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuthController"/> class.
    /// </summary>
    public AuthController(IJwtTokenService jwtTokenService, IOptions<EverTaskApiOptions> options)
    {
        _jwtTokenService = jwtTokenService;
        _options = options.Value;
    }

    /// <summary>
    /// Authenticate with username and password to obtain a JWT token.
    /// </summary>
    /// <param name="request">Login credentials</param>
    /// <returns>JWT token and expiration information</returns>

    [HttpPost("login")]
    [EnableRateLimiting(EverTaskApiOptions.LoginRateLimitPolicyName)]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<LoginResponse> Login([FromBody] LoginRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        // The operate credential is checked FIRST: it is a second account, so a host that configures the
        // same username with a different password still gets the role it asked for.
        if (IsManagementCredential(request))
        {
            return Ok(_jwtTokenService.GenerateToken(request.Username, canManage: true));
        }

        // Validate credentials against configured username/password
        if (request.Username != _options.Username || request.Password != _options.Password)
        {
            return Unauthorized(new { message = "Invalid username or password" });
        }

        // Generate JWT token — read-only: the dashboard credential is shared by everyone who looks at the
        // dashboard, and the management endpoints put handlers with side effects back into execution.
        var response = _jwtTokenService.GenerateToken(request.Username);

        return Ok(response);
    }

    /// <summary>
    /// Validate a JWT token.
    /// </summary>
    /// <param name="request">Token validation request (optional, can be null if token is in Authorization header)</param>
    /// <returns>Token validation result</returns>
    [HttpPost("validate")]
    [ProducesResponseType(typeof(TokenValidationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<TokenValidationResponse> Validate([FromBody] TokenValidationRequest? request = null)
    {
        var token = request?.Token;

        // Try to get token from Authorization header if not in body
        if (string.IsNullOrWhiteSpace(token))
        {
            var authHeader = Request.Headers.Authorization.FirstOrDefault();
            if (authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
            {
                token = authHeader["Bearer ".Length..].Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return BadRequest(new { message = "Token is required (provide in request body or Authorization header)" });
        }

        var response = _jwtTokenService.ValidateToken(token);

        return Ok(response);
    }

    /// <summary>
    /// Authenticate via magic link token supplied in the request body.
    /// Returns a JWT session token if the magic link token is valid.
    /// Magic link must be configured via EverTaskApiOptions.MagicLinkToken.
    /// Preferred over the GET variant: the token never appears in a URL, so it stays
    /// out of server request logs, proxy access logs and browser history.
    /// </summary>
    /// <param name="request">Magic link exchange request</param>
    /// <returns>JWT token and expiration information</returns>
    [HttpPost("magic")]
    [EnableRateLimiting(EverTaskApiOptions.LoginRateLimitPolicyName)]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<LoginResponse> MagicLinkExchange([FromBody] MagicLinkLoginRequest? request = null)
        => ExchangeMagicLinkToken(request?.Token);

    /// <summary>
    /// Authenticate via magic link token supplied in the query string.
    /// Deprecated: the query string is written to server request logs (e.g. Serilog request
    /// logging via RawTarget), reverse-proxy access logs and browser history, permanently
    /// exposing the token. Use POST /api/auth/magic with the token in the body instead.
    /// Kept for backward compatibility with existing ?token= links.
    /// </summary>
    /// <param name="token">The magic link token</param>
    /// <returns>JWT token and expiration information</returns>
    [Obsolete("The query-string form leaks the token into request logs. Use POST /api/auth/magic with the token in the request body.")]
    [HttpGet("magic")]
    [EnableRateLimiting(EverTaskApiOptions.LoginRateLimitPolicyName)]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<LoginResponse> MagicLinkLogin([FromQuery] string? token)
        => ExchangeMagicLinkToken(token);

    private ActionResult<LoginResponse> ExchangeMagicLinkToken(string? token)
    {
        // The response carries a session credential: keep it out of intermediary caches
        Response.Headers.CacheControl = "no-store";

        if (string.IsNullOrEmpty(_options.MagicLinkToken))
        {
            return NotFound(new { message = "Magic link is not configured" });
        }

        if (string.IsNullOrEmpty(token) || !FixedTimeEquals(token, _options.MagicLinkToken))
        {
            return Unauthorized(new { message = "Invalid magic link token" });
        }

        // Generate session JWT — always read-only: a magic link is a URL, and a URL is forwarded, bookmarked
        // and pasted into chats. The operate role is only ever granted by an explicit login.
        var response = _jwtTokenService.GenerateToken(_options.Username);

        return Ok(response);
    }

    /// <summary>
    /// Whether the credentials are the operate-level pair. Both halves must be configured for it to exist,
    /// and both are compared in fixed time: this is the credential that can requeue, resume and cancel.
    /// </summary>
    private bool IsManagementCredential(LoginRequest request) =>
        !string.IsNullOrEmpty(_options.ManagementUsername)
        && !string.IsNullOrEmpty(_options.ManagementPassword)
        && FixedTimeEquals(request.Username, _options.ManagementUsername)
        && FixedTimeEquals(request.Password, _options.ManagementPassword);

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
