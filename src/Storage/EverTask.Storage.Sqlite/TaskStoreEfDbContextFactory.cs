using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EverTask.Storage.Sqlite;

#if DEBUG
//used for migrations
public class TaskStoreEfDbContextFactory : IDesignTimeDbContextFactory<SqliteTaskStoreContext>
{
    public SqliteTaskStoreContext CreateDbContext(string[] args)
    {
        var builder          = new DbContextOptionsBuilder<SqliteTaskStoreContext>();
        var connectionString = "dbcontext";
        builder.UseSqlite(connectionString,
                   opt => opt.MigrationsHistoryTable(HistoryRepository.DefaultTableName, "EverTask"));

        var options = new OptionsWrapper<ITaskStoreOptions>(new SqliteTaskStoreOptions
        {
            AutoApplyMigrations = true
        });

        return new SqliteTaskStoreContext(builder.Options, options);
    }
}
#endif
