using EverTask.Storage.EfCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace EverTask.Tests.Storage;

/// <summary>
/// Covers the public <c>AddAuditCleanup</c> entry-point (the only supported way to enable audit
/// retention). The end-to-end behaviour of the hosted service itself is exercised elsewhere
/// (<see cref="AuditCleanupHostedServiceIntegrationTests"/>); here we pin the DI wiring: the hosted
/// service is registered and the options carry the policy and interval the caller passed.
/// </summary>
public class AuditCleanupRegistrationTests
{
    [Fact]
    public void Should_register_hosted_service_and_configure_options()
    {
        var services = new ServiceCollection();
        var policy   = new AuditRetentionPolicy { StatusAuditRetentionDays = 7 };

        var returned = services.AddAuditCleanup(policy, cleanupIntervalHours: 12);

        // Returns the same collection for fluent chaining.
        returned.ShouldBeSameAs(services);

        // The hosted service is registered as an IHostedService.
        services.ShouldContain(d => d.ServiceType == typeof(IHostedService)
                                 && d.ImplementationType == typeof(AuditCleanupHostedService));

        // The options carry exactly what the caller passed.
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuditCleanupOptions>>().Value;
        options.CleanupInterval.ShouldBe(TimeSpan.FromHours(12));
        options.RetentionPolicy.ShouldBeSameAs(policy);
    }

    [Fact]
    public void Should_default_cleanup_interval_to_24_hours()
    {
        var services = new ServiceCollection();
        services.AddAuditCleanup(new AuditRetentionPolicy());

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuditCleanupOptions>>().Value;
        options.CleanupInterval.ShouldBe(TimeSpan.FromHours(24));
    }

    // Task.Delay rejects delays above uint.MaxValue - 1 ms (~49.7 days); without the clamp the first
    // over-limit wait would crash ExecuteAsync and, with the default BackgroundServiceExceptionBehavior,
    // stop the whole host. A quarterly interval is a plausible way to hit this.
    [Fact]
    public void Should_clamp_intervals_above_the_maximum_timer_duration()
    {
        using var provider = BuildProviderWithIntervals(
            cleanupInterval: TimeSpan.FromDays(90), initialDelay: TimeSpan.FromDays(60));

        var service = provider.GetRequiredService<AuditCleanupHostedService>();

        var max = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
        service.EffectiveCleanupInterval.ShouldBe(max);
        service.EffectiveInitialDelay.ShouldBe(max);
    }

    [Fact]
    public void Should_keep_intervals_below_the_maximum_timer_duration_unchanged()
    {
        using var provider = BuildProviderWithIntervals(
            cleanupInterval: TimeSpan.FromHours(24), initialDelay: TimeSpan.FromMinutes(1));

        var service = provider.GetRequiredService<AuditCleanupHostedService>();

        service.EffectiveCleanupInterval.ShouldBe(TimeSpan.FromHours(24));
        service.EffectiveInitialDelay.ShouldBe(TimeSpan.FromMinutes(1));
    }

    private static ServiceProvider BuildProviderWithIntervals(TimeSpan cleanupInterval, TimeSpan initialDelay)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(AuditCleanupRegistrationTests).Assembly))
                .AddMemoryStorage();
        services.Configure<AuditCleanupOptions>(o =>
        {
            o.RetentionPolicy = new AuditRetentionPolicy { StatusAuditRetentionDays = 7 };
            o.CleanupInterval = cleanupInterval;
            o.InitialDelay    = initialDelay;
        });
        services.AddSingleton<AuditCleanupHostedService>();

        return services.BuildServiceProvider();
    }
}
