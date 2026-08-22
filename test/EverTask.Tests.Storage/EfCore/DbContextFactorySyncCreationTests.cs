using EverTask.Storage.EfCore;
using EverTask.Storage.Postgres;
using EverTask.Storage.Sqlite;
using EverTask.Storage.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Xunit;
#if !NET8_0
using EverTask.Storage.MySql;
#endif

namespace EverTask.Tests.Storage.EfCore;

/// <summary>
/// Issue #33 — the scoped <see cref="ITaskStoreDbContext"/> registration of every provider used to block on
/// the <see cref="ValueTask{TResult}"/> of <see cref="ITaskStoreDbContextFactory.CreateDbContextAsync"/>,
/// which is only defined once the instance has completed. DI resolution has no asynchronous hook, so the
/// factory now exposes a synchronous <see cref="ITaskStoreDbContextFactory.CreateDbContext"/> (default
/// implementation: a well-defined wait on <c>AsTask()</c>) and the registrations use it.
/// No provider connects at registration or creation time, so every case runs without Docker.
/// </summary>
public class DbContextFactorySyncCreationTests
{
    private const string SqlServerConnectionString = "Server=localhost,1;Database=Never;User Id=x;Password=x;TrustServerCertificate=True";
    private const string PostgresConnectionString  = "Host=localhost;Port=1;Database=never;Username=x;Password=x";
#if !NET8_0
    private const string MySqlConnectionString     = "Server=localhost;Port=1;Database=never;User=x;Password=x";
#endif

    public static TheoryData<string> Providers()
    {
        var providers = new TheoryData<string> { "sqlite", "sqlserver", "postgres" };
#if !NET8_0
        providers.Add("mysql");
#endif
        return providers;
    }

    private static void AddStorage(string provider, EverTaskServiceBuilder builder)
    {
        switch (provider)
        {
            case "sqlite":
                builder.AddSqliteStorage("Data Source=:memory:", opt => opt.AutoApplyMigrations = false);
                break;
            case "sqlserver":
                builder.AddSqlServerStorage(SqlServerConnectionString, opt => opt.AutoApplyMigrations = false);
                break;
            case "postgres":
                builder.AddPostgresStorage(PostgresConnectionString, opt => opt.AutoApplyMigrations = false);
                break;
#if !NET8_0
            case "mysql":
                builder.AddMySqlStorage(MySqlConnectionString, opt =>
                {
                    opt.AutoApplyMigrations = false;
                    // An explicit version skips the connect of ServerVersion.AutoDetect
                    opt.ServerVersion = new MariaDbServerVersion(new Version(10, 11));
                });
                break;
#endif
            default:
                throw new ArgumentOutOfRangeException(nameof(provider), provider, null);
        }
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return services;
    }

    [Fact]
    public async Task Should_fall_back_to_async_path_when_implementation_overrides_only_create_async()
    {
        // A third-party factory written against the previous interface shape keeps compiling: the
        // default CreateDbContext consumes the ValueTask as a Task and waits on it — a genuinely
        // asynchronous implementation (suspends before producing) must still come back.
        var context = Mock.Of<ITaskStoreDbContext>();
        ITaskStoreDbContextFactory factory = new AsyncOnlyFactory(context);

        // Off the test's synchronization context, like the DI resolution of a hosted service
        var created = await Task.Run(factory.CreateDbContext).WaitAsync(TimeSpan.FromSeconds(5));

        created.ShouldBeSameAs(context);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Should_resolve_scoped_context_through_sync_path_when_async_path_never_completes(string provider)
    {
        // The registration must never block on CreateDbContextAsync: with a factory whose async
        // path never completes, the old GetAwaiter().GetResult() hung forever (TimeoutException here).
        var context  = Mock.Of<ITaskStoreDbContext>();
        var services = CreateServices();
        // Registered first: the providers use TryAddSingleton for their adapter, so this one wins
        services.AddSingleton<ITaskStoreDbContextFactory>(new SyncOnlyFactory(context));
        AddStorage(provider, services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(DbContextFactorySyncCreationTests).Assembly)));

        await using var sp    = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();
        var             scoped = scope.ServiceProvider;

        var resolved = await Task.Run(scoped.GetRequiredService<ITaskStoreDbContext>)
                                 .WaitAsync(TimeSpan.FromSeconds(5));

        resolved.ShouldBeSameAs(context);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Should_create_context_synchronously_through_provider_adapter(string provider)
    {
        // The in-box adapters override CreateDbContext with the pooled factory's synchronous
        // CreateDbContext: no wait at all, and the scoped registration resolves a real context.
        var services = CreateServices();
        AddStorage(provider, services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(DbContextFactorySyncCreationTests).Assembly)));

        await using var sp      = services.BuildServiceProvider();
        var             factory = sp.GetRequiredService<ITaskStoreDbContextFactory>();

        await using (var created = CreateSynchronously(factory))
            created.ShouldBeAssignableTo<ITaskStoreDbContext>();

        await using var scope = sp.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITaskStoreDbContext>().ShouldBeAssignableTo<DbContext>();
    }

    /// <summary>The synchronous call IS the subject under test (kept out of the async test body).</summary>
    private static DbContext CreateSynchronously(ITaskStoreDbContextFactory factory) =>
        (DbContext)factory.CreateDbContext();

    /// <summary>Implements only the asynchronous member, with a real suspension before producing.</summary>
    private sealed class AsyncOnlyFactory(ITaskStoreDbContext context) : ITaskStoreDbContextFactory
    {
        public async ValueTask<ITaskStoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            return context;
        }
    }

    /// <summary>Synchronous path works; the asynchronous one never completes.</summary>
    private sealed class SyncOnlyFactory(ITaskStoreDbContext context) : ITaskStoreDbContextFactory
    {
        public ValueTask<ITaskStoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            new(new TaskCompletionSource<ITaskStoreDbContext>().Task);

        public ITaskStoreDbContext CreateDbContext() => context;
    }
}
