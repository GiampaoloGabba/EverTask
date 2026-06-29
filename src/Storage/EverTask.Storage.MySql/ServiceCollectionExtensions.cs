using EverTask.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EverTask.Storage.MySql;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers MySQL/MariaDB task storage (Microting EF Core provider). The "schema" is the connection's
    /// database (MySQL has no sub-database schema), so <see cref="MySqlTaskStoreOptions.SchemaName"/> stays
    /// empty and no schema-aware migration plumbing is needed (unlike SQL Server / PostgreSQL).
    /// </summary>
    public static EverTaskServiceBuilder AddMySqlStorage(this EverTaskServiceBuilder builder,
                                                         string connectionString,
                                                         Action<MySqlTaskStoreOptions>? configure = null)
    {
        var storeOptions = new MySqlTaskStoreOptions();
        configure?.Invoke(storeOptions);

        // MySQL/MariaDB have no sub-database schema (a "schema" IS a database, chosen by the connection string).
        // The Initial migration creates unqualified tables and the hot-write stored procedures reference
        // unqualified names, so a non-empty SchemaName would make the runtime model look for schema-qualified
        // tables that do not exist. Reject it loudly instead of silently breaking at the first query.
        if (!string.IsNullOrEmpty(storeOptions.SchemaName))
            throw new ArgumentException(
                "EverTask.Storage.MySql does not support a custom SchemaName: MySQL/MariaDB have no sub-database " +
                "schema. Leave SchemaName empty; the EverTask tables live in the connection string's database.",
                nameof(configure));

        builder.Services.Configure<MySqlTaskStoreOptions>(options =>
        {
            options.SchemaName          = storeOptions.SchemaName;
            options.AutoApplyMigrations = storeOptions.AutoApplyMigrations;
            options.ServerVersion       = storeOptions.ServerVersion;
        });

        builder.Services.AddTransient<IOptions<ITaskStoreOptions>>(sp =>
            sp.GetRequiredService<IOptions<MySqlTaskStoreOptions>>());

        // Explicit version skips a connect; otherwise auto-detect (MySQL vs MariaDB, exact version).
        var serverVersion = storeOptions.ServerVersion ?? ServerVersion.AutoDetect(connectionString);

        // Pooled DbContext factory: contexts are reset and reused instead of allocated per operation,
        // cutting per-write allocation. Schema travels via UseEverTaskSchema because a pooled context may
        // only take a single DbContextOptions ctor parameter (here it is empty: no default schema).
        builder.Services.AddPooledDbContextFactory<MySqlTaskStoreContext>(opt =>
        {
            opt.UseMySql(connectionString, serverVersion)
               .UseEverTaskSchema(storeOptions.SchemaName);
        });

        builder.Services.TryAddSingleton<ITaskStoreDbContextFactory, MySqlDbContextFactoryAdapter>();

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

        // GUID stored as char(36): a UUIDv7 canonical string sorts temporally (timestamp in the leading bytes),
        // matching the (CreatedAtUtc, Id) keyset / recovery index — same v7 family as Postgres/SQLite.
        // NEVER .SqlServer (v8 reorders bytes and would break the string ordering).
        builder.Services.TryAddSingleton<IGuidGenerator>(sp => new DefaultGuidGenerator(UUIDNext.Database.PostgreSql));

        builder.Services.TryAddSingleton<ITaskStorage, MySqlTaskStorage>();
        return builder;
    }
}
