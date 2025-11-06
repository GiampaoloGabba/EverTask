using System.Net;
using System.Text;
using EverTask.Monitor.Api.Options;
using Microsoft.AspNetCore.Http;

namespace EverTask.Monitor.Api.Middleware;

/// <summary>
/// Middleware for HTTP Basic Authentication.
/// Protects API endpoints based on EverTaskApiOptions.
/// </summary>
public class BasicAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly EverTaskApiOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="BasicAuthenticationMiddleware"/> class.
    /// </summary>
    public BasicAuthenticationMiddleware(RequestDelegate next, EverTaskApiOptions options)
    {
        _next = next;
        _options = options;
    }

    /// <summary>
    /// Invokes the middleware.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        // Only protect API paths under BasePath
        var path = context.Request.Path.Value ?? "";
        if (!path.StartsWith(_options.BasePath, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Check IP whitelist first (if configured)
        if (_options.AllowedIpAddresses.Length > 0)
        {
            var clientIp = GetClientIpAddress(context);
            // Fail-secure: block if IP is null or not allowed when whitelist is configured
            if (clientIp == null || !IsIpAllowed(clientIp))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("Access denied: IP address not allowed");
                return;
            }
        }

        // Skip auth if disabled
        if (!_options.RequireAuthentication)
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

        // Allow anonymous read access if configured
        if (_options.AllowAnonymousReadAccess && IsReadOnlyRequest(context.Request))
        {
            await _next(context);
            return;
        }

        // Check for Authorization header
        var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
        if (authHeader == null || !authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            await ChallengeAsync(context);
            return;
        }

        // Decode and validate credentials
        try
        {
            var encodedCredentials = authHeader.Substring("Basic ".Length).Trim();
            var decodedCredentials = Encoding.UTF8.GetString(Convert.FromBase64String(encodedCredentials));
            var credentials = decodedCredentials.Split(':', 2);

            if (credentials.Length == 2 &&
                credentials[0] == _options.Username &&
                credentials[1] == _options.Password)
            {
                await _next(context);
                return;
            }
        }
        catch
        {
            // Invalid base64 or malformed header
        }

        await ChallengeAsync(context);
    }

    private static bool IsReadOnlyRequest(HttpRequest request)
    {
        return request.Method == HttpMethods.Get || request.Method == HttpMethods.Head;
    }

    private static Task ChallengeAsync(HttpContext context)
    {
        context.Response.StatusCode = 401;
        context.Response.Headers.Append("WWW-Authenticate", "Basic realm=\"EverTask Monitoring API\"");
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
