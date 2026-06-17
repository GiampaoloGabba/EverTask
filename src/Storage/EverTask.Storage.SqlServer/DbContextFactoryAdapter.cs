using Microsoft.EntityFrameworkCore;

namespace EverTask.Storage.SqlServer;

/// <summary>
/// Adapts the pooled <see cref="IDbContextFactory{TContext}"/> to <see cref="ITaskStoreDbContextFactory"/>.
/// Backed by AddPooledDbContextFactory: each create leases a reset, reused context (so disposing it returns
/// it to the pool), cutting per-operation allocation (~-88% measured) rather than raw throughput.
/// </summary>
public class SqlServerDbContextFactoryAdapter(IDbContextFactory<SqlServerTaskStoreContext> dbContextFactory) : ITaskStoreDbContextFactory
{
    public async ValueTask<ITaskStoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return dbContext;
    }
}
