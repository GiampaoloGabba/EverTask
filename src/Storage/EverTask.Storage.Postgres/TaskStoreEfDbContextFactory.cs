using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EverTask.Storage.Postgres;

#if DEBUG
// used for migrations (dotnet ef). Schema MUST match the runtime default ("evertask") so the generated
// Initial bakes the correct design-time schema into the model snapshot.
public class TaskStoreEfDbContextFactory : IDesignTimeDbContextFactory<PostgresTaskStoreContext>
{
    public PostgresTaskStoreContext CreateDbContext(string[] args)
    {
        var schema           = new PostgresTaskStoreOptions().SchemaName;
        var builder          = new DbContextOptionsBuilder<PostgresTaskStoreContext>();
        var connectionString = "Host=localhost;Database=evertask_design;Username=postgres;Password=postgres";
        builder.UseNpgsql(connectionString,
                   npg => npg.MigrationsHistoryTable(HistoryRepository.DefaultTableName, schema))
               .ReplaceService<IMigrationsAssembly, DbSchemaAwareMigrationAssembly>()
               .UseEverTaskSchema(schema);

        return new PostgresTaskStoreContext(builder.Options);
    }
}
#endif
