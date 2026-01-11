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
    [EnableRateLimiting("login")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<LoginResponse> Login([FromBody] LoginRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        // Validate credentials against configured username/password
        if (request.Username != _options.Username || request.Password != _options.Password)
        {
            return Unauthorized(new { message = "Invalid username or password" });
        }

        // Generate JWT token
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
        string? token = request?.Token;

        // Try to get token from Authorization header if not in body
        if (string.IsNullOrWhiteSpace(token))
        {
            var authHeader = Request.Headers.Authorization.FirstOrDefault();
            if (authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
            {
                token = authHeader.Substring("Bearer ".Length).Trim();
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
    /// Authenticate via magic link token.
    /// Returns a JWT session token if the magic link token is valid.
    /// Magic link must be configured via EverTaskApiOptions.MagicLinkToken.
    /// </summary>
    /// <param name="token">The magic link token</param>
    /// <returns>JWT token and expiration information</returns>
    [HttpGet("magic")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<LoginResponse> MagicLinkLogin([FromQuery] string? token)
    {
        if (string.IsNullOrEmpty(_options.MagicLinkToken))
        {
            return NotFound(new { message = "Magic link is not configured" });
        }

        if (string.IsNullOrEmpty(token) || !string.Equals(token, _options.MagicLinkToken, StringComparison.Ordinal))
        {
            return Unauthorized(new { message = "Invalid magic link token" });
        }

        // Generate session JWT
        var response = _jwtTokenService.GenerateToken(_options.Username);

        return Ok(response);
    }
}
