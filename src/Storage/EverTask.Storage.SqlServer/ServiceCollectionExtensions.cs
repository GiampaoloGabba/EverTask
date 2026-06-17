using EverTask.Abstractions;
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

        // Pooled DbContext factory: contexts are reset and reused instead of allocated per operation,
        // cutting per-write allocation (~-88% measured). Schema travels via UseEverTaskSchema because a
        // pooled context may only take a single DbContextOptions ctor parameter.
        builder.Services.AddPooledDbContextFactory<SqlServerTaskStoreContext>(opt =>
        {
            opt.UseSqlServer(connectionString,
                   sqlOpt => sqlOpt.MigrationsHistoryTable(HistoryRepository.DefaultTableName, storeOptions.SchemaName))
               .ReplaceService<IMigrationsAssembly, DbSchemaAwareMigrationAssembly>()
               .UseEverTaskSchema(storeOptions.SchemaName);
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
            using var provider  = builder.Services.BuildServiceProvider();
            using var scope     = provider.CreateScope();
            var       dbContext = scope.ServiceProvider.GetRequiredService<ITaskStoreDbContext>();
            ((DbContext)dbContext).Database.Migrate();
        }

        // Register SQL Server-optimized GUID generator (UUID v8)
        builder.Services.TryAddSingleton<IGuidGenerator>(sp => new DefaultGuidGenerator(UUIDNext.Database.SqlServer));

        builder.Services.TryAddSingleton<ITaskStorage, SqlServerTaskStorage>();
        return builder;
    }
}
