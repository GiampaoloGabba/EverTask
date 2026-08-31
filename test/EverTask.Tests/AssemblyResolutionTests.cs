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
    public void Should_resolve_handler_when_closed_handler_is_discovered()
    {
        _provider.GetService<IEverTaskHandler<TestTaskRequest>>().ShouldNotBeNull();
    }

    [Fact]
    public void Should_resolve_handler_when_handler_type_is_internal()
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
    public void Should_throw_when_registered_assembly_is_null()
    {
        // Config-boundary validation: without it a null only surfaces later as a
        // NullReferenceException inside the assembly scan. ParamName is asserted so a LINQ-thrown
        // ArgumentNullException ("source") could never pass for the boundary check.
        var config = new EverTaskServiceConfiguration();

        Should.Throw<ArgumentNullException>(() => config.RegisterTasksFromAssembly(null!))
              .ParamName.ShouldBe("assembly");
        Should.Throw<ArgumentNullException>(() => config.RegisterTasksFromAssemblies(null!))
              .ParamName.ShouldBe("assemblies");
        Should.Throw<ArgumentException>(
                  () => config.RegisterTasksFromAssemblies(typeof(TestTaskRequest).Assembly, null!))
              .ParamName.ShouldBe("assemblies");

        // The mixed valid/null call is atomic: nothing was registered.
        config.AssembliesToRegister.ShouldBeEmpty();
    }

    [Fact]
    public void Should_resolve_first_handler_when_duplicate_closed_handlers_are_discovered()
    {
        var handlers = _provider.GetServices<IEverTaskHandler<TestTaskRequest>>().ToArray();
        handlers.Length.ShouldBe(1);
        handlers[0].ShouldBeOfType<TestTaskHanlder>();
    }

    [Fact]
    public void Should_warn_when_duplicate_closed_handlers_are_discovered()
    {
        // G2: TestTaskRequest has two handlers (TestTaskHanlder + TestTaskHanlderDuplicate). First-wins
        // registration stays, but the ambiguity must no longer be silent — a warning is recorded.
        var config   = _provider.GetRequiredService<EverTaskServiceConfiguration>();
        var warnings = config.HandlerRegistrationWarnings;

        warnings.ShouldContain(w =>
            w.Contains(nameof(TestTaskRequest)) && w.Contains(nameof(TestTaskHanlderDuplicate)));
    }

    [Fact]
    public void Should_warn_and_not_register_when_handler_type_is_open_generic()
    {
        // G1: open-generic handlers were silently dropped (the closing path was dead code). They must
        // be surfaced with a warning instead of vanishing without trace — and never auto-closed.
        var config   = _provider.GetRequiredService<EverTaskServiceConfiguration>();
        var warnings = config.HandlerRegistrationWarnings;

        warnings.ShouldContain(w => w.Contains(nameof(OpenGenericRegistrationHandler<object>)));
        _provider.GetService<IEverTaskHandler<OpenGenericRegistrationTask<int>>>().ShouldBeNull();
    }
}
