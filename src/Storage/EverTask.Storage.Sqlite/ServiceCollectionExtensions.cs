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

        builder.Services.AddDbContext<SqliteTaskStoreContext>((_, opt) =>
        {
            opt.UseSqlite(connectionString);
        });

        builder.Services.AddScoped<ITaskStoreDbContext>(provider =>
            provider.GetRequiredService<SqliteTaskStoreContext>());

        if (storeOptions.AutoApplyMigrations)
        {
            using var scope     = builder.Services.BuildServiceProvider().CreateScope();
            var       dbContext = scope.ServiceProvider.GetRequiredService<SqliteTaskStoreContext>();
            dbContext.Database.Migrate();
        }

        builder.Services.TryAddSingleton<ITaskStorage, EfCoreTaskStorage>();
        return builder;
    }
}
