namespace EverTask.Storage.SqlServer;

public class SqlServerTaskStoreContext(
    DbContextOptions<SqlServerTaskStoreContext> options,
    IOptions<ITaskStoreOptions> storeOptions) : TaskStoreEfDbContext<SqlServerTaskStoreContext>(options, storeOptions);
