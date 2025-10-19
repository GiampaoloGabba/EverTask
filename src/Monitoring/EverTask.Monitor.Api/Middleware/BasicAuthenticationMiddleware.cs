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
        // Skip auth if disabled
        if (!_options.RequireAuthentication)
        {
            await _next(context);
            return;
        }

        // Only protect API paths under BasePath
        var path = context.Request.Path.Value ?? "";
        if (!path.StartsWith(_options.BasePath, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Allow anonymous access to config endpoint
        if (path.Equals($"{_options.BasePath}/config", StringComparison.OrdinalIgnoreCase))
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
}
