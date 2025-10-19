using EverTask.Dispatcher;
using EverTask.Logger;
using EverTask.Scheduler;

namespace EverTask.Tests;

public class AssemblyResolutionTests
{
    private readonly IServiceProvider _provider;

    public AssemblyResolutionTests()
    {
        IServiceCollection services = new ServiceCollection();
        services.AddLogging(); // Required for WorkerQueueManager
        services.AddEverTask(cfg => cfg.RegisterTasksFromAssembly(typeof(TestTaskRequest).Assembly));
        _provider = services.BuildServiceProvider();
    }

    [Fact]
    public void Should_resolve_Configuration()
    {
        _provider.GetService<EverTaskServiceConfiguration>().ShouldNotBeNull();
    }

    [Fact]
    public void Should_resolve_Logger()
    {
        _provider.GetService<IEverTaskLogger<object>>().ShouldNotBeNull();
    }

    [Fact]
    public void Should_resolve_WorkerBlackList()
    {
        _provider.GetService<IWorkerBlacklist>().ShouldNotBeNull();
    }

    [Fact]
    public void Should_resolve_WorkerQueue()
    {
        _provider.GetService<IWorkerQueue>().ShouldNotBeNull();
    }

    [Fact]
    public void Should_resolve_Scheduler()
    {
        _provider.GetService<IScheduler>().ShouldNotBeNull();
    }

    [Fact]
    public void Should_resolve_TaskDipatcher_Internal()
    {
        _provider.GetService<ITaskDispatcherInternal>().ShouldNotBeNull();
    }

    [Fact]
    public void Should_resolve_TaskDipatcher()
    {
        _provider.GetService<ITaskDispatcher>().ShouldNotBeNull();
    }

    [Fact]
    public void Should_resolve_WorkerExecutor()
    {
        _provider.GetService<IEverTaskWorkerExecutor>().ShouldNotBeNull();
    }

    [Fact]
    public void Should_resolve_HostedService()
    {
        _provider.GetService<IHostedService>().ShouldNotBeNull();
    }

    [Fact]
    public void Should_resolve_RequestHandler()
    {
        _provider.GetService<IEverTaskHandler<TestTaskRequest>>().ShouldNotBeNull();
    }

    [Fact]
    public void Should_resolve_internal_Handler()
    {
        _provider.GetService<IEverTaskHandler<InternalTestTaskRequest>>().ShouldNotBeNull();
    }

    [Fact]
    public void Should_require_atleast_one_Assembly()
    {
        var services = new ServiceCollection();
        Action registration = () => services.AddEverTask(_ => {});
        registration.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void Should_resolve_first_duplicate_Handler()
    {
        var handlers = _provider.GetServices<IEverTaskHandler<TestTaskRequest>>().ToArray();
        handlers.Length.ShouldBe(1);
        handlers[0].ShouldBeOfType<TestTaskHanlder>();
    }


    [Fact]
    public void CouldCloseTo_Should_ReturnFalse_ForIncompatibleTypes()
    {
        var openType            = typeof(OpenGenericClass<>);
        var closedInterfaceType = typeof(ITestInterface<string>); // Incompatibile con OpenGenericClass<T>

        var result = openType.CouldCloseTo(closedInterfaceType);

        Assert.False(result);
    }

    [Fact]
    public void CouldCloseTo_Should_ReturnFalse_ForNonGenericTypes()
    {
        var nonGenericType      = typeof(ClosedGenericClass);
        var closedInterfaceType = typeof(ITestInterface<int>);

        var result = nonGenericType.CouldCloseTo(closedInterfaceType);

        Assert.False(result);
    }
}


public interface ITestInterface<T> { }

public class OpenGenericClass<T> : ITestInterface<T> { }
public class ClosedGenericClass : ITestInterface<int> { }
