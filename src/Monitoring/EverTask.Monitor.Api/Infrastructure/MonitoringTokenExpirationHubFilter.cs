using EverTask.Monitor.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;

namespace EverTask.Monitor.Api.Infrastructure;

internal sealed class MonitoringTokenExpirationHubFilter(IJwtTokenService jwtTokenService) : IHubFilter
{
    private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    public Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var httpContext = context.Context.GetHttpContext();
        var token       = httpContext is null ? null : TokenOf(httpContext);
        if (!string.IsNullOrEmpty(token))
        {
            var validation = jwtTokenService.ValidateToken(token);

            if (validation is { IsValid: true, ExpiresAt: { } expiresAt })
                _ = AbortAtExpirationAsync(context.Context, expiresAt);
        }

        return next(context);
    }

    private static string? TokenOf(HttpContext context)
    {
        if (context.Request.Query.TryGetValue("access_token", out var fromQuery) &&
            !string.IsNullOrEmpty(fromQuery))
        {
            return fromQuery.ToString();
        }

        var header = context.Request.Headers.Authorization.FirstOrDefault();

        return header?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
                   ? header["Bearer ".Length..].Trim()
                   : null;
    }

    private static async Task AbortAtExpirationAsync(HubCallerContext context, DateTimeOffset expiresAt)
    {
        try
        {
            while (!context.ConnectionAborted.IsCancellationRequested)
            {
                var remaining = expiresAt - DateTimeOffset.UtcNow;

                if (remaining <= TimeSpan.Zero)
                    break;

                await Task.Delay(remaining > MaximumTimerDelay ? MaximumTimerDelay : remaining,
                    context.ConnectionAborted).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (context.ConnectionAborted.IsCancellationRequested)
        {
            return;
        }

        if (!context.ConnectionAborted.IsCancellationRequested)
            context.Abort();
    }
}
