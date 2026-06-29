namespace EverTask.Storage.MySql;

public class MySqlTaskStoreContext(DbContextOptions<MySqlTaskStoreContext> options)
    : TaskStoreEfDbContext<MySqlTaskStoreContext>(options);
