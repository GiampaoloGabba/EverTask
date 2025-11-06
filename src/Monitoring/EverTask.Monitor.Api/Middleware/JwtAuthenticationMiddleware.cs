using System.Net;
using EverTask.Monitor.Api.Options;
using EverTask.Monitor.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace EverTask.Monitor.Api.Middleware;

/// <summary>
/// Middleware for JWT authentication.
/// Protects API endpoints based on EverTaskApiOptions.
/// </summary>
public class JwtAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly EverTaskApiOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="JwtAuthenticationMiddleware"/> class.
    /// </summary>
    public JwtAuthenticationMiddleware(RequestDelegate next, EverTaskApiOptions options)
    {
        _next = next;
        _options = options;
    }

    /// <summary>
    /// Invokes the middleware.
    /// Protection layers:
    /// 1. IP whitelist (if configured) -> applies to ALL /evertask-monitoring paths
    /// 2. JWT authentication (if enabled) -> applies only to /api and /hub paths
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // Determine path types
        var isMonitoringPath = path.StartsWith(_options.BasePath, StringComparison.OrdinalIgnoreCase);
        var isApiPath = path.StartsWith(_options.ApiBasePath, StringComparison.OrdinalIgnoreCase);
        var isHubPath = path.StartsWith(_options.SignalRHubPath, StringComparison.OrdinalIgnoreCase);

        // Skip if not a monitoring path
        if (!isMonitoringPath)
        {
            await _next(context);
            return;
        }

        // LAYER 1: IP WHITELIST - applies to ALL /evertask-monitoring paths (API + Hub + UI)
        if (_options.AllowedIpAddresses.Length > 0)
        {
            var clientIp = GetClientIpAddress(context);
            // Fail-secure: block if IP is null or not allowed when whitelist is configured
            if (clientIp == null || !IsIpAllowed(clientIp))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("Access denied");
                return;
            }
        }

        // LAYER 2: JWT AUTHENTICATION - only applies to API and Hub (not UI static files)
        var requiresJwt = isApiPath || isHubPath;

        if (!requiresJwt)
        {
            // UI path - no JWT required (only IP protection)
            await _next(context);
            return;
        }

        // Skip JWT auth if disabled
        if (!_options.EnableAuthentication)
        {
            await _next(context);
            return;
        }

        // Allow anonymous access to config endpoint
        if (path.Equals($"{_options.ApiBasePath}/config", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Allow anonymous access to auth endpoints (login/validate)
        if (path.Equals($"{_options.ApiBasePath}/auth/login", StringComparison.OrdinalIgnoreCase) ||
            path.Equals($"{_options.ApiBasePath}/auth/validate", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // JWT AUTHENTICATION - required for API and Hub
        string? token = null;

        // Try Authorization header first
        var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
        if (authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
        {
            token = authHeader.Substring("Bearer ".Length).Trim();
        }
        // For SignalR, also check query string (SignalR sends token as ?access_token=...)
        else if (isHubPath && context.Request.Query.TryGetValue("access_token", out var accessToken))
        {
            token = accessToken.ToString();
        }

        if (string.IsNullOrEmpty(token))
        {
            // No Bearer token provided
            await ChallengeAsync(context);
            return;
        }

        var jwtService = context.RequestServices.GetRequiredService<IJwtTokenService>();
        var validation = jwtService.ValidateToken(token);

        if (!validation.IsValid)
        {
            // Invalid JWT token
            await ChallengeAsync(context);
            return;
        }

        // JWT token is valid, proceed
        await _next(context);
    }

    private static bool IsReadOnlyRequest(HttpRequest request)
    {
        return request.Method == HttpMethods.Get || request.Method == HttpMethods.Head;
    }

    private static Task ChallengeAsync(HttpContext context)
    {
        context.Response.StatusCode = 401;
        context.Response.Headers.Append("WWW-Authenticate", "Bearer realm=\"EverTask Monitoring API\"");
        return Task.CompletedTask;
    }

    private static IPAddress? GetClientIpAddress(HttpContext context)
    {
        // Check X-Forwarded-For header first (reverse proxy scenario)
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            var ips = forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (ips.Length > 0 && IPAddress.TryParse(ips[0], out var forwardedIp))
            {
                return forwardedIp;
            }
        }

        // Fallback to direct connection IP, or ::1 (localhost IPv6) if null (test scenarios)
        return context.Connection.RemoteIpAddress ?? IPAddress.IPv6Loopback;
    }

    private bool IsIpAllowed(IPAddress clientIp)
    {
        foreach (var allowedEntry in _options.AllowedIpAddresses)
        {
            // Check for CIDR notation (e.g., "192.168.0.0/24")
            if (allowedEntry.Contains('/'))
            {
                if (IsIpInCidrRange(clientIp, allowedEntry))
                {
                    return true;
                }
            }
            // Check for exact IP match
            else if (IPAddress.TryParse(allowedEntry, out var allowedIp) && clientIp.Equals(allowedIp))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIpInCidrRange(IPAddress clientIp, string cidr)
    {
        try
        {
            var parts = cidr.Split('/');
            if (parts.Length != 2)
                return false;

            if (!IPAddress.TryParse(parts[0], out var networkIp))
                return false;

            if (!int.TryParse(parts[1], out var prefixLength))
                return false;

            // Convert IPs to bytes
            var clientBytes = clientIp.GetAddressBytes();
            var networkBytes = networkIp.GetAddressBytes();

            // Must be same address family (IPv4/IPv6)
            if (clientBytes.Length != networkBytes.Length)
                return false;

            // Calculate mask
            var maskBytes = new byte[networkBytes.Length];
            for (var i = 0; i < maskBytes.Length; i++)
            {
                var bitsInByte = Math.Min(8, Math.Max(0, prefixLength - (i * 8)));
                maskBytes[i] = (byte)(0xFF << (8 - bitsInByte));
            }

            // Compare masked addresses
            for (var i = 0; i < clientBytes.Length; i++)
            {
                if ((clientBytes[i] & maskBytes[i]) != (networkBytes[i] & maskBytes[i]))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
