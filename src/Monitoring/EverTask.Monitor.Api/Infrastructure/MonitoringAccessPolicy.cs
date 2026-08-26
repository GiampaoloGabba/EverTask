using System.Net;
using EverTask.Monitor.Api.Options;
using EverTask.Monitor.Api.Services;
using Microsoft.AspNetCore.Http;

namespace EverTask.Monitor.Api.Infrastructure;

/// <summary>Which part of the monitoring surface a path belongs to.</summary>
internal enum MonitoringSurface
{
    /// <summary>Not the monitoring surface: nothing here applies.</summary>
    None,

    /// <summary>Dashboard files, the OpenAPI document and whatever a companion package serves: IP only.</summary>
    Ui,

    /// <summary>The REST API: IP, then JWT unless the path is one of the anonymous ones.</summary>
    Api,

    /// <summary>The SignalR hub: IP, then JWT, and the handshake may carry it in the query string.</summary>
    Hub
}

/// <summary>What a caller may do with the monitoring surface.</summary>
internal enum MonitoringAccess
{
    Allowed,
    IpBlocked,
    Unauthenticated
}

/// <summary>
/// The ONE place that decides whether a request may reach the monitoring surface: which surface a path
/// belongs to, the IP whitelist and the JWT check.
/// </summary>
/// <remarks>
/// It judges a path that is RELATIVE to the path base, which is what routing resolved and therefore where
/// the surface really is. <c>JwtAuthenticationMiddleware</c> runs before a host's <c>UsePathBase</c> and can
/// only hand it a path that still carries the base, so the middleware is an outer shield and the
/// endpoint-level enforcement (the MVC filter and the endpoint guard) is what actually protects the surface.
/// </remarks>
internal sealed class MonitoringAccessPolicy(EverTaskApiOptions options, IJwtTokenService jwtTokenService)
{
    /// <summary>The API paths that must answer before a caller can possibly hold a token.</summary>
    private readonly string[] _anonymousApiPaths =
    [
        $"{options.ApiBasePath}/config",
        $"{options.ApiBasePath}/auth/login",
        $"{options.ApiBasePath}/auth/validate",
        $"{options.ApiBasePath}/auth/magic"
    ];

    /// <summary>
    /// Segment-wise, never a string prefix: <c>/evertask-monitoring-x</c> is not the monitoring surface, and
    /// treating it as one would put the host's own route behind this policy.
    /// </summary>
    public MonitoringSurface SurfaceOf(PathString path)
    {
        if (!path.StartsWithSegments(options.BasePath))
            return MonitoringSurface.None;

        if (path.StartsWithSegments(options.SignalRHubPath))
            return MonitoringSurface.Hub;

        return path.StartsWithSegments(options.ApiBasePath) ? MonitoringSurface.Api : MonitoringSurface.Ui;
    }

    /// <summary>Whether the write surface is switched off and this path belongs to it.</summary>
    public bool IsDisabledManagementPath(PathString path) =>
        !options.EnableManagementEndpoints && path.StartsWithSegments(options.ManagementBasePath);

    public MonitoringAccess Evaluate(HttpContext context, PathString path)
    {
        var surface = SurfaceOf(path);

        if (surface == MonitoringSurface.None)
            return MonitoringAccess.Allowed;

        // Fail-secure, and before authentication: a whitelist covers the dashboard files too.
        if (options.AllowedIpAddresses.Length > 0 && !IsIpAllowed(ClientIpOf(context)))
            return MonitoringAccess.IpBlocked;

        if (surface == MonitoringSurface.Ui || !options.EnableAuthentication)
            return MonitoringAccess.Allowed;

        if (surface == MonitoringSurface.Api && _anonymousApiPaths.Contains(path.Value ?? "",
                StringComparer.OrdinalIgnoreCase))
        {
            return MonitoringAccess.Allowed;
        }

        var token = TokenOf(context, surface);

        return !string.IsNullOrEmpty(token) && jwtTokenService.ValidateToken(token).IsValid
                   ? MonitoringAccess.Allowed
                   : MonitoringAccess.Unauthenticated;
    }

    /// <summary>Writes the refusal an evaluation asked for. Shared so every enforcement point answers alike.</summary>
    public static Task RefuseAsync(HttpContext context, MonitoringAccess access)
    {
        if (access == MonitoringAccess.IpBlocked)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return context.Response.WriteAsync("Access denied");
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.Append("WWW-Authenticate", ManagementAuthorizationFilter.BearerChallenge);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The bearer token, from the header — or, on the hub alone, from the query string: the SignalR
    /// handshake cannot set headers on the WebSocket upgrade.
    /// </summary>
    private static string? TokenOf(HttpContext context, MonitoringSurface surface)
    {
        var header = context.Request.Headers.Authorization.FirstOrDefault();

        if (header?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
            return header["Bearer ".Length..].Trim();

        return surface == MonitoringSurface.Hub && context.Request.Query.TryGetValue("access_token", out var fromQuery)
                   ? fromQuery.ToString()
                   : null;
    }

    private static IPAddress ClientIpOf(HttpContext context)
    {
        // Check X-Forwarded-For header first (reverse proxy scenario)
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();

        if (!string.IsNullOrEmpty(forwardedFor))
        {
            var ips = forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (ips.Length > 0 && IPAddress.TryParse(ips[0], out var forwardedIp))
                return forwardedIp;
        }

        // Fallback to direct connection IP, or ::1 (localhost IPv6) if null (test scenarios)
        return context.Connection.RemoteIpAddress ?? IPAddress.IPv6Loopback;
    }

    private bool IsIpAllowed(IPAddress clientIp)
    {
        foreach (var allowedEntry in options.AllowedIpAddresses)
        {
            if (allowedEntry.Contains('/'))
            {
                if (IsIpInCidrRange(clientIp, allowedEntry))
                    return true;
            }
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

            var clientBytes  = clientIp.GetAddressBytes();
            var networkBytes = networkIp.GetAddressBytes();

            // Must be same address family (IPv4/IPv6)
            if (clientBytes.Length != networkBytes.Length)
                return false;

            var maskBytes = new byte[networkBytes.Length];

            for (var i = 0; i < maskBytes.Length; i++)
            {
                var bitsInByte = Math.Min(8, Math.Max(0, prefixLength - (i * 8)));
                maskBytes[i] = (byte)(0xFF << (8 - bitsInByte));
            }

            return Enumerable.Range(0, clientBytes.Length)
                             .All(i => (clientBytes[i] & maskBytes[i]) == (networkBytes[i] & maskBytes[i]));
        }
        catch
        {
            return false;
        }
    }
}
