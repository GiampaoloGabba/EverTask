using Microsoft.EntityFrameworkCore.Design;

namespace EverTask.Storage.SqlServer;

#if DEBUG
//used for migrations
public class TaskStoreEfDbContextFactory : IDesignTimeDbContextFactory<SqlServerTaskStoreContext>
{
    public SqlServerTaskStoreContext CreateDbContext(string[] args)
    {
        var schema           = new SqlServerTaskStoreOptions().SchemaName;
        var builder          = new DbContextOptionsBuilder<SqlServerTaskStoreContext>();
        var connectionString = "dbcontext";
        builder.UseSqlServer(connectionString,
                   opt => opt.MigrationsHistoryTable(HistoryRepository.DefaultTableName, schema))
               .ReplaceService<IMigrationsAssembly, DbSchemaAwareMigrationAssembly>()
               .UseEverTaskSchema(schema);

        return new SqlServerTaskStoreContext(builder.Options);
    }
}
#endif
