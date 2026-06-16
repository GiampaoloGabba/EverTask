using Microsoft.Data.Sqlite;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace EverTask.LoadHarness.Infra;

/// <summary>
/// Provisions the external bits a storage backend needs — a SQLite temp file in WAL mode, or a Docker
/// container for SqlServer/Postgres — and hands back the connection string + a cleanup. Shared by
/// <see cref="StorageMatrix"/> (standalone storage, for A4-storage/A3) and <see cref="HostFactory"/>
/// (full host, for L8/L-dispatch-prod) so the container/temp-file plumbing lives in one place.
/// </summary>
public static class StorageProvisioner
{
    public static async Task<Provisioned> ProvisionAsync(string storage, CancellationToken ct)
    {
        switch (storage)
        {
            case "inmemory":
                return new Provisioned("inmemory", null, "In-Memory", () => ValueTask.CompletedTask);

            case "sqlite":
                return ProvisionSqlite();

            case "sqlserver":
            {
                var c = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
                await c.StartAsync(ct);
                return new Provisioned("sqlserver", c.GetConnectionString(), "SqlServer (Testcontainers)",
                                       async () => await c.DisposeAsync());
            }

            case "postgres":
            {
                var c = new PostgreSqlBuilder("postgres:16-alpine").Build();
                await c.StartAsync(ct);
                return new Provisioned("postgres", c.GetConnectionString(), "Postgres (Testcontainers)",
                                       async () => await c.DisposeAsync());
            }

            default:
                throw new ArgumentException($"Unknown storage '{storage}'. Use inmemory|sqlite|sqlserver|postgres.");
        }
    }

    private static Provisioned ProvisionSqlite()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"evertask-bench-{Guid.NewGuid():N}.db");
        string cs = $"Data Source={dbPath};Cache=Shared";

        // Keep-alive connection holds the WAL/SHM live for the run; closed and removed on cleanup.
        var keepAlive = new SqliteConnection(cs);
        keepAlive.Open();
        using (var pragma = keepAlive.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }

        return new Provisioned("sqlite", cs, $"SQLite (WAL, {dbPath})", () =>
        {
            keepAlive.Close();
            keepAlive.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try { if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix); }
                catch { /* best effort */ }
            }
            return ValueTask.CompletedTask;
        });
    }
}

/// <summary>A provisioned backend: its kind, connection string (null for in-memory), a label, and cleanup.</summary>
public sealed record Provisioned(string Kind, string? ConnectionString, string Description, Func<ValueTask> Cleanup);
