using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EverTask.Storage.Sqlite;

public static class ServiceCollectionExtensions
{
    public static EverTaskServiceBuilder AddSqliteStorage(this EverTaskServiceBuilder builder,
                                                          string connectionString = "Data Source=EverTask.db",
                                                          Action<SqliteTaskStoreOptions>? configure = null)
    {
        var storeOptions = new SqliteTaskStoreOptions();
        configure?.Invoke(storeOptions);

        builder.Services.Configure<SqliteTaskStoreOptions>(options =>
        {
            options.SchemaName          = storeOptions.SchemaName;
            options.AutoApplyMigrations = storeOptions.AutoApplyMigrations;
        });

        builder.Services.AddTransient<IOptions<ITaskStoreOptions>>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<SqliteTaskStoreOptions>>();
            return options;
        });

        // Register IDbContextFactory for DbContext creation with built-in pooling
        // Pool size automatically managed by EF Core (typically cores * 2)
        builder.Services.AddDbContextFactory<SqliteTaskStoreContext>(opt =>
        {
            opt.UseSqlite(connectionString);
        });

        // Register high-performance factory using IDbContextFactory
        builder.Services.TryAddSingleton<ITaskStoreDbContextFactory, SqliteDbContextFactoryAdapter>();

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

        // Register SQLite-optimized GUID generator (UUID v7)
        builder.Services.TryAddSingleton<IGuidGenerator>(sp => new DefaultGuidGenerator(UUIDNext.Database.SQLite));

        builder.Services.TryAddSingleton<ITaskStorage, SqliteTaskStorage>();
        return builder;
    }
}
