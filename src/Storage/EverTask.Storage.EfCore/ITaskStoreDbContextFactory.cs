namespace EverTask.Storage.EfCore;

/// <summary>
/// Factory abstraction for creating <see cref="ITaskStoreDbContext"/> instances.
/// Provider packages supply the concrete implementation (typically an
/// IDbContextFactory-backed adapter that benefits from DbContext pooling).
/// </summary>
public interface ITaskStoreDbContextFactory
{
    /// <summary>
    /// Creates a new <see cref="ITaskStoreDbContext"/> instance.
    /// The caller is responsible for disposing the context.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A new <see cref="ITaskStoreDbContext"/> instance</returns>
    ValueTask<ITaskStoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default);
}
