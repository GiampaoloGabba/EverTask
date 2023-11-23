using EverTask.Storage.EfCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EverTask.Storage.SqlServer;

public static class ServiceCollectionExtensions
{
    public static EverTaskServiceBuilder AddSqlServerStorage(this EverTaskServiceBuilder builder,
                                                             string connectionString,
                                                             Action<TaskStoreOptions>? configure = null)
    {
        var storeOptions = new TaskStoreOptions();
        configure?.Invoke(storeOptions);

        builder.Services.Configure<TaskStoreOptions>(options =>
        {
            options.SchemaName = storeOptions.SchemaName;
            options.AutoApplyMigrations = storeOptions.AutoApplyMigrations;
        });

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
