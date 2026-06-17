namespace EverTask.Storage.Postgres;

public class PostgresTaskStoreContext(DbContextOptions<PostgresTaskStoreContext> options)
    : TaskStoreEfDbContext<PostgresTaskStoreContext>(options);
