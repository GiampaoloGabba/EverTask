using Testcontainers.MsSql;

namespace EverTask.Tests.Storage;

/// <summary>
/// The one SQL Server container of this assembly, shared by every suite that needs one.
/// </summary>
/// <remarks>
/// A SQL Server instance reserves about 5120 asynchronous I/O contexts from the Linux kernel as it boots, out
/// of the 65536 a default <c>fs.aio-max-nr</c> grants the whole Docker VM. That budget is what forces the
/// sharing: a container per suite, times the three target frameworks a solution-wide <c>dotnet test</c> runs,
/// overruns it — and an instance that cannot get its contexts does not degrade, it aborts during boot
/// ("Unable to create a new asynchronous I/O context", status 0x0000000b) and the container exits, so
/// Testcontainers reports a wait strategy failing on a container that is not running. The failure lands on
/// whichever suite happens to start last, which is why it reads as flakiness rather than as a budget.
/// <para>
/// Sharing is safe because every suite that uses this carries <c>[Collection("DatabaseTests")]</c> — they
/// never run concurrently — and each resets the database with Respawn before each of its tests, so a suite
/// still starts from an empty schema. Migrations are idempotent: whichever suite runs first applies them.
/// </para>
/// <para>
/// The container is deliberately never disposed: it lives as long as the test process, and Ryuk removes it
/// on exit.
/// </para>
/// </remarks>
internal static class SqlServerTestContainer
{
    private const string Image = "mcr.microsoft.com/mssql/server:2022-latest";

    private static readonly SemaphoreSlim StartGate = new(1, 1);
    private static MsSqlContainer? _container;

    /// <summary>Starts the container on first use and returns the connection string to it.</summary>
    public static async Task<string> GetConnectionStringAsync()
    {
        if (_container is not null)
            return _container.GetConnectionString();

        await StartGate.WaitAsync();
        try
        {
            if (_container is null)
            {
                var container = new MsSqlBuilder(Image).Build();
                await container.StartAsync();
                _container = container;
            }
        }
        finally
        {
            StartGate.Release();
        }

        return _container.GetConnectionString();
    }

    /// <summary>
    /// Blocking overload for the one caller that cannot await: the synchronous <c>Initialize()</c> of the
    /// shared EF Core storage suite.
    /// </summary>
    public static string GetConnectionString() => GetConnectionStringAsync().GetAwaiter().GetResult();
}
