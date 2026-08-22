using EverTask.Storage.EfCore;
using EverTask.Storage.SqlServer;

namespace EverTask.Tests.Logging;

public class LoggerMessageEventIdTests
{
    [Fact]
    public void Should_have_explicit_unique_event_ids_when_scanning_core_abstractions_and_efcore_assemblies() =>
        LoggerMessageEventIdAssert.AssertExplicitAndUnique(
            typeof(WorkerExecutor).Assembly,
            typeof(IEverTask).Assembly,
            typeof(EfCoreTaskStorage).Assembly,
            typeof(SqlServerTaskStorage).Assembly);
}
