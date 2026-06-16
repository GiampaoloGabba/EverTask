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
}
