using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EverTask.Storage.SqlServer;

public static class ServiceCollectionExtensions
{
    public static EverTaskServiceBuilder AddSqlServerStorage(this EverTaskServiceBuilder builder,
                                                             string connectionString,
                                                             Action<SqlServerTaskStoreOptions>? configure = null)
    {
        var storeOptions = new SqlServerTaskStoreOptions();
        configure?.Invoke(storeOptions);

        builder.Services.Configure<SqlServerTaskStoreOptions>(options =>
        {
            options.SchemaName          = storeOptions.SchemaName;
            options.AutoApplyMigrations = storeOptions.AutoApplyMigrations;
        });

        builder.Services.AddTransient<IOptions<ITaskStoreOptions>>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<SqlServerTaskStoreOptions>>();
            return options;
        });

        builder.Services.AddDbContext<SqlServerTaskStoreContext>((_, opt) =>
        {
            opt.UseSqlServer(connectionString,
                   opt => opt.MigrationsHistoryTable(HistoryRepository.DefaultTableName, storeOptions.SchemaName))
               .ReplaceService<IMigrationsAssembly, DbSchemaAwareMigrationAssembly>();
        });

        builder.Services.AddScoped<ITaskStoreDbContext>(provider =>
            provider.GetRequiredService<SqlServerTaskStoreContext>());

        if (storeOptions.AutoApplyMigrations)
        {
            using var scope     = builder.Services.BuildServiceProvider().CreateScope();
            var       dbContext = scope.ServiceProvider.GetRequiredService<SqlServerTaskStoreContext>();
            dbContext.Database.Migrate();
        }

        builder.Services.TryAddSingleton<ITaskStorage, EfCoreTaskStorage>();
        return builder;
    }
}
