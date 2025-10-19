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

        // Register IDbContextFactory for DbContext creation with built-in pooling
        // Pool size automatically managed by EF Core (typically cores * 2)
        builder.Services.AddDbContextFactory<SqlServerTaskStoreContext>(opt =>
        {
            opt.UseSqlServer(connectionString,
                   sqlOpt => sqlOpt.MigrationsHistoryTable(HistoryRepository.DefaultTableName, storeOptions.SchemaName))
               .ReplaceService<IMigrationsAssembly, DbSchemaAwareMigrationAssembly>();
        });

        // Register high-performance factory using IDbContextFactory
        builder.Services.TryAddSingleton<ITaskStoreDbContextFactory, SqlServerDbContextFactoryAdapter>();

        // Register ITaskStoreDbContext for backward compatibility (uses factory internally)
        builder.Services.AddScoped<ITaskStoreDbContext>(provider =>
        {
            var factory = provider.GetRequiredService<ITaskStoreDbContextFactory>();
            return factory.CreateDbContextAsync().GetAwaiter().GetResult();
        });

        if (storeOptions.AutoApplyMigrations)
        {
            using var scope     = builder.Services.BuildServiceProvider().CreateScope();
            var       dbContext = scope.ServiceProvider.GetRequiredService<ITaskStoreDbContext>();
            ((DbContext)dbContext).Database.Migrate();
        }

        builder.Services.TryAddSingleton<ITaskStorage, EfCoreTaskStorage>();
        return builder;
    }
}
