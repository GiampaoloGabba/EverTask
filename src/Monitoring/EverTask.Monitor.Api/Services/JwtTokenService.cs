using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using EverTask.Monitor.Api.DTOs.Auth;
using EverTask.Monitor.Api.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EverTask.Monitor.Api.Services;

/// <summary>
/// Service for generating and validating JWT tokens.
/// </summary>
public class JwtTokenService : IJwtTokenService
{
    private readonly EverTaskApiOptions _options;
    private readonly ILogger<JwtTokenService> _logger;
    private readonly string _secret;
    private readonly SymmetricSecurityKey _signingKey;

    public JwtTokenService(IOptions<EverTaskApiOptions> options, ILogger<JwtTokenService> logger)
    {
        _options = options.Value;
        _logger = logger;

        // Generate or validate JWT secret
        if (string.IsNullOrWhiteSpace(_options.JwtSecret))
        {
            _secret = GenerateRandomSecret();
            _logger.LogWarning(
                "JWT secret not configured. Generated random secret. " +
                "This is NOT recommended for production or multi-instance deployments. " +
                "Configure JwtSecret in EverTaskApiOptions."
            );
        }
        else
        {
            _secret = _options.JwtSecret;

            // Validate minimum secret length (256 bits / 32 bytes)
            if (Encoding.UTF8.GetByteCount(_secret) < 32)
            {
                _logger.LogWarning(
                    "JWT secret is shorter than recommended minimum (32 bytes / 256 bits). " +
                    "Consider using a stronger secret for production environments."
                );
            }
        }

        _signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secret));
    }

    /// <inheritdoc />
    public LoginResponse GenerateToken(string username)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddHours(_options.JwtExpirationHours);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, username),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
        };

        var token = new JwtSecurityToken(
            issuer: _options.JwtIssuer,
            audience: _options.JwtAudience,
            claims: claims,
            notBefore: now.DateTime,
            expires: expiresAt.DateTime,
            signingCredentials: new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256)
        );

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

        _logger.LogInformation("Generated JWT token for user '{Username}' (expires: {ExpiresAt})", username, expiresAt);

        return new LoginResponse(tokenString, expiresAt, username);
    }

    /// <inheritdoc />
    public TokenValidationResponse ValidateToken(string token)
    {
        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();

            // Disable claim type mapping to preserve original JWT claim types
            tokenHandler.InboundClaimTypeMap.Clear();

            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = _options.JwtIssuer,
                ValidAudience = _options.JwtAudience,
                IssuerSigningKey = _signingKey,
                ClockSkew = TimeSpan.FromMinutes(5) // Allow 5 minutes clock skew
            };

            var principal = tokenHandler.ValidateToken(token, validationParameters, out var validatedToken);

            // Extract username from claims (using original JWT claim type)
            var username = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

            // Extract expiration
            var jwtToken = (JwtSecurityToken)validatedToken;
            var expiresAt = jwtToken.ValidTo != DateTime.MinValue
                ? new DateTimeOffset(jwtToken.ValidTo, TimeSpan.Zero)
                : (DateTimeOffset?)null;

            _logger.LogDebug("JWT token validated successfully for user '{Username}'", username);

            return new TokenValidationResponse(true, username, expiresAt);
        }
        catch (SecurityTokenExpiredException ex)
        {
            _logger.LogDebug("JWT token expired: {Message}", ex.Message);
            return new TokenValidationResponse(false, null, null);
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogDebug("JWT token validation failed: {Message}", ex.Message);
            return new TokenValidationResponse(false, null, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error during JWT token validation");
            return new TokenValidationResponse(false, null, null);
        }
    }

    private static string GenerateRandomSecret()
    {
        // Generate 32 bytes (256 bits) random secret
        var bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes);
    }
}
