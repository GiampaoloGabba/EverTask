using EverTask.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EverTask.Storage.Postgres;

public static class ServiceCollectionExtensions
{
    public static EverTaskServiceBuilder AddPostgresStorage(this EverTaskServiceBuilder builder,
                                                            string connectionString,
                                                            Action<PostgresTaskStoreOptions>? configure = null)
    {
        var storeOptions = new PostgresTaskStoreOptions();
        configure?.Invoke(storeOptions);

        builder.Services.Configure<PostgresTaskStoreOptions>(options =>
        {
            options.SchemaName          = storeOptions.SchemaName;
            options.AutoApplyMigrations = storeOptions.AutoApplyMigrations;
        });

        builder.Services.AddTransient<IOptions<ITaskStoreOptions>>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<PostgresTaskStoreOptions>>();
            return options;
        });

        // Pooled DbContext factory: contexts are reset and reused instead of allocated per operation,
        // cutting per-write allocation (~-88% measured). Schema travels via UseEverTaskSchema because a
        // pooled context may only take a single DbContextOptions ctor parameter.
        builder.Services.AddPooledDbContextFactory<PostgresTaskStoreContext>(opt =>
        {
            opt.UseNpgsql(connectionString,
                   npg => npg.MigrationsHistoryTable(HistoryRepository.DefaultTableName, storeOptions.SchemaName))
               .ReplaceService<IMigrationsAssembly, DbSchemaAwareMigrationAssembly>()
               .UseEverTaskSchema(storeOptions.SchemaName);
        });

        // Register high-performance factory using IDbContextFactory.
        builder.Services.TryAddSingleton<ITaskStoreDbContextFactory, PostgresDbContextFactoryAdapter>();

        // Register ITaskStoreDbContext for backward compatibility (uses factory internally).
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

        // PostgreSQL sorts uuid byte-wise from byte 0; UUIDv7 keeps inserts sequential -> NEVER .SqlServer here.
        builder.Services.TryAddSingleton<IGuidGenerator>(sp => new DefaultGuidGenerator(UUIDNext.Database.PostgreSql));

        builder.Services.TryAddSingleton<ITaskStorage, PostgresTaskStorage>();
        return builder;
    }
}
