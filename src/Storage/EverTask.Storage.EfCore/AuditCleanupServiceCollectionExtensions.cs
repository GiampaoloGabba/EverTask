using EverTask.Storage.EfCore;
using Microsoft.Extensions.DependencyInjection;

namespace EverTask;

/// <summary>
/// Extension methods for registering audit cleanup services.
/// </summary>
public static class AuditCleanupServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="AuditCleanupHostedService"/> to enforce audit trail retention policies.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="retentionPolicy">
    /// The retention policy to apply. Use <see cref="AuditRetentionPolicy.WithUniformRetention"/>
    /// for simple TTL or <see cref="AuditRetentionPolicy.WithErrorPriority"/> to keep errors longer.
    /// </param>
    /// <param name="cleanupIntervalHours">
    /// Interval in hours between cleanup cycles (default: 24 hours).
    /// </param>
    /// <returns>The service collection for method chaining.</returns>
    /// <example>
    /// <code>
    /// services.AddEverTask(opt => opt.RegisterTasksFromAssembly(assembly))
    ///     .AddSqlServerStorage(connectionString)
    ///     .AddAuditCleanup(
    ///         AuditRetentionPolicy.WithErrorPriority(
    ///             successRetentionDays: 7,
    ///             errorRetentionDays: 90),
    ///         cleanupIntervalHours: 24);
    /// </code>
    /// </example>
    public static IServiceCollection AddAuditCleanup(
        this IServiceCollection services,
        AuditRetentionPolicy retentionPolicy,
        int cleanupIntervalHours = 24)
    {
        // Configure cleanup options
        services.Configure<AuditCleanupOptions>(options =>
        {
            options.CleanupInterval = TimeSpan.FromHours(cleanupIntervalHours);
            options.RetentionPolicy = retentionPolicy;
        });

        // Register hosted service
        services.AddHostedService<AuditCleanupHostedService>();

        return services;
    }
}
