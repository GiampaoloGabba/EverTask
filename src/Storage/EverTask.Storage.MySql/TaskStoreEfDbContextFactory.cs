using Microsoft.EntityFrameworkCore.Design;

namespace EverTask.Storage.MySql;

#if DEBUG
// Used for migrations (dotnet ef). Scaffolding does NOT connect to a database, but UseMySql still requires a
// ServerVersion — hardcode MariaDB 10.11 (the supported LTS target). Schema is empty (MySQL "schema" == database).
public class TaskStoreEfDbContextFactory : IDesignTimeDbContextFactory<MySqlTaskStoreContext>
{
    public MySqlTaskStoreContext CreateDbContext(string[] args)
    {
        var builder          = new DbContextOptionsBuilder<MySqlTaskStoreContext>();
        var connectionString = "Server=localhost;Database=evertask_design;User=root;Password=root";
        builder.UseMySql(connectionString, new MariaDbServerVersion(new Version(10, 11)))
               .UseEverTaskSchema("");

        return new MySqlTaskStoreContext(builder.Options);
    }
}
#endif
