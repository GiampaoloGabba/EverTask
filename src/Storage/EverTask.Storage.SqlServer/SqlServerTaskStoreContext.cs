namespace EverTask.Storage.SqlServer;

public class SqlServerTaskStoreContext(DbContextOptions<SqlServerTaskStoreContext> options)
    : TaskStoreEfDbContext<SqlServerTaskStoreContext>(options);
