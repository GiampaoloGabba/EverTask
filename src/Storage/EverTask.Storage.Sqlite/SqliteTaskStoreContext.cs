namespace EverTask.Storage.Sqlite;

public class SqliteTaskStoreContext(
    DbContextOptions<SqliteTaskStoreContext> options,
    IOptions<ITaskStoreOptions> storeOptions) : TaskStoreEfDbContext<SqliteTaskStoreContext>(options, storeOptions);
