using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using EverTask.EfCore;
using EverTask.SqlServer;
using EverTask.Storage;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static EverTaskServiceBuilder AddSqlServerStorage(this EverTaskServiceBuilder builder,
                                                             string connectionString,
                                                             Action<TaskStoreOptions>? configure = null)
    {
        var storeOptions = new TaskStoreOptions();
        configure?.Invoke(storeOptions);
        builder.Services.TryAddSingleton(storeOptions);

        builder.Services.AddDbContext<TaskStoreEfDbContext>((_, opt) =>
        {
            opt.UseSqlServer(connectionString,
                   opt => opt.MigrationsHistoryTable(HistoryRepository.DefaultTableName, storeOptions.SchemaName))
               .ReplaceService<IMigrationsAssembly, DbSchemaAwareMigrationAssembly>();
        });

        builder.Services.AddScoped<ITaskStoreDbContext>(provider => provider.GetRequiredService<TaskStoreEfDbContext>());

        if (storeOptions.AutoApplyMigrations)
        {
            using var scope     = builder.Services.BuildServiceProvider().CreateScope();
            var       dbContext = scope.ServiceProvider.GetRequiredService<TaskStoreEfDbContext>();
            dbContext.Database.Migrate();
        }

        builder.Services.TryAddSingleton<ITaskStorage, EfCoreTaskStorage>();
        return builder;
    }
}
