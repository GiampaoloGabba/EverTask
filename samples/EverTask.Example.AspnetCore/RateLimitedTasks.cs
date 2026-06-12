using EverTask.Abstractions;

namespace EverTask.Example.AspnetCore;

/// <summary>
/// Keyed rate limiting demo: simulates calls to an external API limited per tenant.
/// The task carries the throttling key (the tenant id); the handler declares the policy.
/// Tasks of different tenants never block each other; over-budget tasks are re-scheduled
/// at their reserved slot without holding a worker.
/// </summary>
public record SyncTenantDataTask(string TenantId, int CallNumber) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => TenantId;
}

public class SyncTenantDataTaskHandler : EverTaskHandler<SyncTenantDataTask>
{
    private readonly ILogger<SyncTenantDataTaskHandler> _logger;

    public SyncTenantDataTaskHandler(ILogger<SyncTenantDataTaskHandler> logger)
    {
        _logger = logger;
    }

    // Each tenant gets 3 "external API calls" per 10 seconds; Burst=1 keeps them evenly
    // spaced (one every ~3.3s) so the throttling is easy to observe in the logs/dashboard
    public override RateLimitPolicy? RateLimitPolicy =>
        new(3, TimeSpan.FromSeconds(10)) { Burst = 1 };

    public override async Task Handle(SyncTenantDataTask task, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "[RateLimit demo] Tenant {TenantId} call #{CallNumber} hitting the external API at {Time:HH:mm:ss.fff}",
            task.TenantId, task.CallNumber, DateTimeOffset.UtcNow);

        // Simulated external API call
        await Task.Delay(150, cancellationToken);
    }
}
