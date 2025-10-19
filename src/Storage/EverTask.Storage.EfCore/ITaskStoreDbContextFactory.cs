using Microsoft.Extensions.DependencyInjection;

namespace EverTask.Storage.EfCore;

/// <summary>
/// Factory interface for creating ITaskStoreDbContext instances.
/// Abstracts the creation logic to allow both IServiceScopeFactory (legacy)
/// and IDbContextFactory<T> (high-performance) implementations.
/// </summary>
public interface ITaskStoreDbContextFactory
{
    /// <summary>
    /// Creates a new ITaskStoreDbContext instance.
    /// The caller is responsible for disposing the context.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A new ITaskStoreDbContext instance</returns>
    ValueTask<ITaskStoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// IServiceScopeFactory-based implementation of ITaskStoreDbContextFactory (legacy).
/// Creates a service scope for each DbContext instance.
/// Less efficient than DbContextFactory-based implementation due to full scope creation overhead.
/// </summary>
public class ServiceScopeDbContextFactory(IServiceScopeFactory serviceScopeFactory) : ITaskStoreDbContextFactory
{
    public ValueTask<ITaskStoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        var scope = serviceScopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ITaskStoreDbContext>();
        return ValueTask.FromResult(dbContext);
    }
}
