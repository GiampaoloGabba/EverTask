using EverTask.Storage.EfCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;

namespace EverTask.Storage.SqlServer;

#if DEBUG
//used for migrations
public class TaskStoreEfDbContextFactory : IDesignTimeDbContextFactory<TaskStoreEfDbContext>
{
    public TaskStoreEfDbContext CreateDbContext(string[] args)
    {
        var builder          = new DbContextOptionsBuilder<TaskStoreEfDbContext>();
        var connectionString = "dbcontext";
        builder.UseSqlServer(connectionString,
                   opt => opt.MigrationsHistoryTable(HistoryRepository.DefaultTableName, "EverTask"))
               .ReplaceService<IMigrationsAssembly, DbSchemaAwareMigrationAssembly>();

        var options = new OptionsWrapper<TaskStoreOptions>(new TaskStoreOptions
        {
            AutoApplyMigrations = true
        });

        return new TaskStoreEfDbContext(builder.Options, options);
    }
}
#endif
