namespace EverTask.Storage.Postgres;

public class PostgresTaskStoreContext(
    DbContextOptions<PostgresTaskStoreContext> options,
    IOptions<ITaskStoreOptions> storeOptions) : TaskStoreEfDbContext<PostgresTaskStoreContext>(options, storeOptions);
