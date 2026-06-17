namespace EverTask.Storage.Sqlite;

public class SqliteTaskStoreContext(DbContextOptions<SqliteTaskStoreContext> options)
    : TaskStoreEfDbContext<SqliteTaskStoreContext>(options);
