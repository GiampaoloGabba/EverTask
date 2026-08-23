using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EverTask.Storage.Sqlite;

#if DEBUG
//used for migrations
public class TaskStoreEfDbContextFactory : IDesignTimeDbContextFactory<SqliteTaskStoreContext>
{
    public SqliteTaskStoreContext CreateDbContext(string[] args)
    {
        // SQLite has no schema concept, so SqliteTaskStoreOptions.SchemaName is "" — and the EF tooling
        // rejects an EMPTY schema argument (null is the way to say "no schema").
        var schema           = string.IsNullOrEmpty(new SqliteTaskStoreOptions().SchemaName)
                                   ? null
                                   : new SqliteTaskStoreOptions().SchemaName;
        var builder          = new DbContextOptionsBuilder<SqliteTaskStoreContext>();
        var connectionString = "dbcontext";
        builder.UseSqlite(connectionString,
                   opt => opt.MigrationsHistoryTable(HistoryRepository.DefaultTableName, schema))
               .UseEverTaskSchema(schema);

        return new SqliteTaskStoreContext(builder.Options);
    }
}
#endif
