using EverTask.Tests.Logging;

namespace EverTask.Tests.Monitoring;

public class LoggerMessageEventIdTests
{
    [Fact]
    public void Should_have_explicit_unique_event_ids_when_scanning_core_and_monitoring_assemblies() =>
        LoggerMessageEventIdAssert.AssertExplicitAndUnique(
            typeof(WorkerExecutor).Assembly,
            typeof(IEverTask).Assembly,
            typeof(JwtTokenService).Assembly,
            typeof(SignalRTaskMonitor).Assembly,
            typeof(EverTask.Monitor.Api.Scalar.Extensions.ServiceCollectionExtensions).Assembly);
}
