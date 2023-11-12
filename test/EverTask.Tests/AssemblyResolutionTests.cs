using EverTask.Logger;

namespace EverTask.Tests;

public class AssemblyResolutionTests
{
    private readonly IServiceProvider _provider;

    public AssemblyResolutionTests()
    {
        IServiceCollection services = new ServiceCollection();
        services.AddEverTask(cfg => cfg.RegisterTasksFromAssembly(typeof(TestTaskRequest).Assembly));
        _provider = services.BuildServiceProvider();
    }

    [Fact]
    public void Should_resolve_TaskDipatcher()
    {
        _provider.GetService<ITaskDispatcher>().ShouldNotBeNull();
    }

    [Fact]
    public void Should_resolve_WorkerQueue()
    {
        _provider.GetService<IWorkerQueue>().ShouldNotBeNull();
    }

    [Fact]
    public void Should_resolve_Logger()
    {
        _provider.GetService<IEverTaskLogger<object>>().ShouldNotBeNull();
    }

    [Fact]
    public void Should_resolve_Configuration()
    {
        _provider.GetService<EverTaskServiceConfiguration>().ShouldNotBeNull();
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
}
