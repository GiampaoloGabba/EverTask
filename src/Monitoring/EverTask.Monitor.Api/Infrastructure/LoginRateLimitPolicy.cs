using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;

namespace EverTask.Monitor.Api.Infrastructure;

/// <summary>
/// Per-client fixed-window rate limit for the dashboard login endpoint (5 attempts / 15 minutes,
/// no queueing), registered under <c>EverTaskApiOptions.LoginRateLimitPolicyName</c>. Partitioned
/// by remote IP so one client cannot exhaust the budget of the others. Rejections return 429.
/// Enforced only when the host pipeline runs <c>UseRateLimiter()</c> (issue #21).
/// </summary>
internal sealed class LoginRateLimitPolicy : IRateLimiterPolicy<string>
{
    public RateLimitPartition<string> GetPartition(HttpContext httpContext) =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                Window               = TimeSpan.FromMinutes(15),
                PermitLimit          = 5,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 0
            });

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected { get; } =
        (context, _) =>
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return ValueTask.CompletedTask;
        };
}
