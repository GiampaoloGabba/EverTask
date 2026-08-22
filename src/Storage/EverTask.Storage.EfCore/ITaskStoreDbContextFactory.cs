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

    /// <summary>
    /// Creates a new <see cref="ITaskStoreDbContext"/> instance synchronously. Used where no asynchronous
    /// path exists, such as the DI resolution of the scoped <see cref="ITaskStoreDbContext"/> registration.
    /// The caller is responsible for disposing the context.
    /// </summary>
    /// <remarks>
    /// The default implementation consumes <see cref="CreateDbContextAsync"/> as a <see cref="Task"/> and
    /// blocks on it: a well-defined sync-over-async wait, never a blocking read of an incomplete
    /// <see cref="ValueTask{TResult}"/>. Implementations backed by a synchronous source (the in-box adapters
    /// wrap EF Core's <c>IDbContextFactory&lt;TContext&gt;.CreateDbContext</c>) override it to avoid the
    /// wait altogether.
    /// </remarks>
    /// <returns>A new <see cref="ITaskStoreDbContext"/> instance</returns>
    ITaskStoreDbContext CreateDbContext() =>
        CreateDbContextAsync().AsTask().GetAwaiter().GetResult();
}
