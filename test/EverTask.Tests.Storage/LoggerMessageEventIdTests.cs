using EverTask.Storage.EfCore;
using EverTask.Storage.Postgres;
using EverTask.Storage.Sqlite;
using EverTask.Storage.SqlServer;
using EverTask.Tests.Logging;
using EverTask.Worker;
using Xunit;

namespace EverTask.Tests.Storage;

public class LoggerMessageEventIdTests
{
    [Fact]
    public void Should_have_explicit_unique_event_ids_when_scanning_all_storage_assemblies() =>
        LoggerMessageEventIdAssert.AssertExplicitAndUnique(
            typeof(WorkerExecutor).Assembly,
            typeof(EfCoreTaskStorage).Assembly,
            typeof(SqlServerTaskStorage).Assembly,
            typeof(PostgresTaskStorage).Assembly,
            typeof(SqliteTaskStorage).Assembly
#if !NET8_0
            , typeof(EverTask.Storage.MySql.MySqlTaskStorage).Assembly
#endif
        );
}
