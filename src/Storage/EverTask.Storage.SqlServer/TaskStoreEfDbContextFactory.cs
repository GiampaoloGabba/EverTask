using Microsoft.EntityFrameworkCore.Design;

namespace EverTask.Storage.SqlServer;

#if DEBUG
//used for migrations
public class TaskStoreEfDbContextFactory : IDesignTimeDbContextFactory<SqlServerTaskStoreContext>
{
    public SqlServerTaskStoreContext CreateDbContext(string[] args)
    {
        var builder          = new DbContextOptionsBuilder<SqlServerTaskStoreContext>();
        var connectionString = "dbcontext";
        builder.UseSqlServer(connectionString,
                   opt => opt.MigrationsHistoryTable(HistoryRepository.DefaultTableName, "EverTask"))
               .ReplaceService<IMigrationsAssembly, DbSchemaAwareMigrationAssembly>();

        var options = new OptionsWrapper<ITaskStoreOptions>(new SqlServerTaskStoreOptions
        {
            AutoApplyMigrations = true
        });

        return new SqlServerTaskStoreContext(builder.Options, options);
    }
}
#endif
