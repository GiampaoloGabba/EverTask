using EverTask;
using EverTask.Abstractions;
using EverTask.Configuration;
using EverTask.Dispatcher;
using EverTask.Handler;
using EverTask.Logger;
using EverTask.Monitoring;
using EverTask.Scheduler;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Scheduler.Recurring.Builder;
using EverTask.Storage;
using EverTask.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EverTask.ConsumerCompatibility.Baseline;

/// <summary>
/// Everything an application built on the previous release touches, called for real.
/// </summary>
/// <remarks>
/// Each probe is a plain call compiled against the baseline metadata. Run against the current assemblies it
/// either succeeds — the member is still there with the same signature — or throws
/// <see cref="MissingMemberException"/> / <see cref="TypeLoadException"/>, which is what the test reports.
/// Growing an optional parameter on an existing method or constructor is enough to produce the second
/// outcome, which is why they are all exercised individually rather than through one happy path.
/// </remarks>
public static class BaselineConsumer
{
    public sealed record ProbeTask(string Name) : IEverTask;

    /// <summary>Builds every fluent shape and reads the resulting definition back.</summary>
    public static void BuildEverySchedule()
    {
        var probes = new Action<IRecurringTaskBuilder>[]
        {
            b => b.Schedule().EverySecond(),
            b => b.Schedule().EveryMinute().AtSecond(30),
            b => b.Schedule().EveryHour().AtMinute(15),
            b => b.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)),
            b => b.Schedule().EveryDay().AtTimes(new TimeOnly(9, 0), new TimeOnly(18, 0)),
            b => b.Schedule().EveryWeek().OnDay(DayOfWeek.Monday).AtTime(new TimeOnly(7, 30)),
            b => b.Schedule().EveryWeek().OnDays(DayOfWeek.Monday, DayOfWeek.Friday).AtTime(new TimeOnly(7, 30)),
            b => b.Schedule().EveryMonth().OnDay(1).AtTime(new TimeOnly(0, 30)),
            b => b.Schedule().EveryMonth().OnFirst(DayOfWeek.Monday).AtTime(new TimeOnly(6, 0)),
            b => b.Schedule().OnDays(DayOfWeek.Tuesday).AtTime(new TimeOnly(3, 0)),
            b => b.Schedule().OnMonths(1, 6).OnDay(2).AtTime(new TimeOnly(4, 0)),
            b => b.Schedule().Every(5).Seconds(),
            b => b.Schedule().Every(5).Minutes().AtSecond(10),
            b => b.Schedule().Every(2).Hours().AtMinute(5),
            b => b.Schedule().Every(3).Days().AtTime(new TimeOnly(12, 0)),
            b => b.Schedule().Every(2).Weeks().OnDay(DayOfWeek.Sunday).AtTime(new TimeOnly(1, 0)),
            b => b.Schedule().Every(2).Months().OnDay(15).AtTime(new TimeOnly(2, 0)),
            b => b.Schedule().UseCron("0 3 * * *"),
            b => b.RunNow().Then().EverySecond(),
            b => b.RunDelayed(TimeSpan.FromMinutes(1)).Then().EveryMinute(),
            b => b.RunAt(DateTimeOffset.UtcNow.AddHours(1)).Then().EveryHour(),
            b => b.Schedule().EverySecond().RunUntil(DateTimeOffset.UtcNow.AddDays(1)),
            b => b.Schedule().EverySecond().MaxRuns(3),
            b => b.Schedule().EveryDay().RunUntil(DateTimeOffset.UtcNow.AddDays(1)),
            b => b.Schedule().EveryHour().RunUntil(DateTimeOffset.UtcNow.AddDays(1)),
            b => b.Schedule().EveryMinute().RunUntil(DateTimeOffset.UtcNow.AddDays(1)),
            b => b.Schedule().EveryMonth().RunUntil(DateTimeOffset.UtcNow.AddDays(1)),
            b => b.Schedule().EveryWeek().OnDay(DayOfWeek.Monday).RunUntil(DateTimeOffset.UtcNow.AddDays(1))
        };

        foreach (var probe in probes)
        {
            var builder = new RecurringTaskBuilderProbe();
            probe(builder);
            builder.Definition.ToString();
        }
    }

    /// <summary>
    /// The concrete builders, constructed directly: they are public types with public constructors, so an
    /// application is free to new one up instead of going through <c>Schedule()</c>.
    /// </summary>
    public static void ConstructEveryBuilderDirectly()
    {
        var task = new RecurringTask { SecondInterval = new SecondInterval(1) };

        _ = new IntervalSchedulerBuilder(task);
        _ = new EverySchedulerBuilder(task, 5);
        _ = new BuildableSchedulerBuilder(task);
        _ = new ThenableSchedulerBuilder(task);

        _ = new MinuteSchedulerBuilder(new RecurringTask { MinuteInterval = new MinuteInterval(1) });
        _ = new HourSchedulerBuilder(new RecurringTask { HourInterval = new HourInterval(1) });
        _ = new DailyTimeSchedulerBuilder(new RecurringTask { DayInterval = new DayInterval(1) });
        _ = new WeeklySchedulerBuilder(new RecurringTask { WeekInterval = new WeekInterval(1) });
        _ = new MonthlySchedulerBuilder(new RecurringTask { MonthInterval = new MonthInterval(1) });
    }

    /// <summary>The occurrence math a consumer can call on its own.</summary>
    public static (DateTimeOffset? NextRun, TimeSpan Interval, int Skipped) ComputeOccurrences()
    {
        var definition = new RecurringTask { MinuteInterval = new MinuteInterval(10) };
        var anchor     = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

        var next     = definition.CalculateNextRun(anchor, 0);
        var recovery = definition.CalculateNextRun(anchor, 1, true);
        var interval = definition.GetMinimumInterval();

        var result = definition.CalculateNextValidRun(anchor, 1, anchor.AddHours(2));
        var noCount = definition.CalculateNextValidRun(anchor, 1, anchor.AddHours(2), false, false);

        _ = recovery;
        _ = noCount;
        return (next, interval, result.SkippedCount);
    }

    /// <summary>The public records: positional construction, deconstruction and <c>with</c>.</summary>
    public static (Guid Id, string Type) UsePublicRecords()
    {
        var executor = new TaskHandlerExecutor(
            new ProbeTask("probe"),
            null,
            "Handler, Assembly",
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            null,
            null,
            Guid.NewGuid(),
            "default",
            "probe-key",
            AuditLevel.Full);

        var (task, _, handlerTypeName, _, _, _, _, _, _, persistenceId, _, _, _, _, _, _) = executor;
        var copy = executor with { QueueName = "recurring" };

        var row = copy.ToQueuedTask();

        var evt = new EverTaskEventData(persistenceId, DateTimeOffset.UtcNow, "Information",
            task.GetType().ToString(), handlerTypeName!, "{}", "message");
        var (eventTaskId, _, _, _, _, _, _, _, _) = evt;

        _ = eventTaskId;
        return (row.Id, row.Type);
    }

    /// <summary>A storage written against the previous interface, driven through the interface.</summary>
    public static async Task<int> DriveTaskStorage()
    {
        ITaskStorage storage = new BaselineTaskStorage();

        var row = new QueuedTask
        {
            Id           = Guid.NewGuid(),
            Type         = "T",
            Request      = "{}",
            Handler      = "H",
            Status       = QueuedTaskStatus.Queued,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        await storage.Persist(row).ConfigureAwait(false);
        await storage.SetQueued(row.Id, AuditLevel.Full).ConfigureAwait(false);
        await storage.SetInProgress(row.Id, AuditLevel.Full).ConfigureAwait(false);
        await storage.SetCompleted(row.Id, 1, AuditLevel.Full).ConfigureAwait(false);
        await storage.UpdateCurrentRun(row.Id, 1, DateTimeOffset.UtcNow, AuditLevel.Full).ConfigureAwait(false);
        await storage.CompleteRecurringRun(row.Id, 1, DateTimeOffset.UtcNow, AuditLevel.Full).ConfigureAwait(false);
        await storage.SetRecurringSeriesCompleted(row.Id, 1, AuditLevel.Full).ConfigureAwait(false);
        await storage.SetRecurringTaskPoisoned(row.Id, new InvalidOperationException("x"), AuditLevel.Full)
                     .ConfigureAwait(false);
        _ = await storage.TrySetQueuedIfRecoverable(row.Id, AuditLevel.Full).ConfigureAwait(false);
        _ = await storage.IncrementRecoveryFailure(row.Id).ConfigureAwait(false);
        await storage.ClearRecoveryFailure(row.Id).ConfigureAwait(false);

        var pending = await storage.RetrievePending(null, null, 10).ConfigureAwait(false);
        return pending.Length;
    }

    /// <summary>The two schedulers and the worker queue, constructed the way the previous release exposed them.</summary>
    public static void ConstructRuntimeComponents()
    {
        var queueManager = new BaselineQueueManager();

        using (var scheduler = new PeriodicTimerScheduler(queueManager,
                   new BaselineLogger<PeriodicTimerScheduler>(), TimeSpan.FromMinutes(5)))
        {
            _ = scheduler.IsScheduled(Guid.NewGuid());
        }

        using (var sharded = new ShardedScheduler(queueManager, new BaselineLogger<ShardedScheduler>(),
                   null, 2))
        {
            _ = sharded.IsScheduled(Guid.NewGuid());
        }

        var queue = new WorkerQueue(new QueueConfiguration { Name = "probe" }, new BaselineLogger<WorkerQueue>(),
            new BaselineBlacklist());
        _ = queue.Count;
    }

    /// <summary>
    /// The two hosted components. They are normally built by the container, but both are public classes with
    /// public constructors, and the container picks a constructor by the same signatures a consumer would.
    /// </summary>
    public static void ConstructHostedComponents(IServiceProvider provider)
    {
        var executor = new WorkerExecutor(
            provider.GetRequiredService<IWorkerBlacklist>(),
            provider.GetRequiredService<EverTaskServiceConfiguration>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IScheduler>(),
            provider.GetRequiredService<ICancellationSourceProvider>(),
            provider.GetRequiredService<IEverTaskLogger<WorkerExecutor>>(),
            provider.GetRequiredService<ILoggerFactory>());

        using var service = new WorkerService(
            provider.GetRequiredService<IWorkerQueueManager>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ITaskDispatcherInternal>(),
            provider.GetRequiredService<EverTaskServiceConfiguration>(),
            executor,
            provider.GetRequiredService<IEverTaskLogger<WorkerService>>());
    }

    /// <summary>The dispatcher surface, through the public interface only.</summary>
    public static async Task UseDispatcher(ITaskDispatcher dispatcher)
    {
        var probe = new ProbeTask("dispatch");

        _ = await dispatcher.Dispatch(probe).ConfigureAwait(false);
        _ = await dispatcher.Dispatch(probe, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        _ = await dispatcher.Dispatch(probe, DateTimeOffset.UtcNow.AddMinutes(5)).ConfigureAwait(false);

        var recurringId = await dispatcher.Dispatch(probe, b => b.Schedule().EveryHour().AtMinute(5))
                                          .ConfigureAwait(false);
        await dispatcher.Cancel(recurringId).ConfigureAwait(false);
    }

    private sealed class RecurringTaskBuilderProbe : IRecurringTaskBuilder
    {
        public readonly RecurringTask Definition = new();

        public IThenableSchedulerBuilder RunNow()
        {
            Definition.RunNow = true;
            return RunAt(DateTimeOffset.UtcNow);
        }

        public IThenableSchedulerBuilder RunDelayed(TimeSpan delay)
        {
            Definition.InitialDelay = delay;
            return new ThenableSchedulerBuilder(Definition);
        }

        public IThenableSchedulerBuilder RunAt(DateTimeOffset dateTimeOffset)
        {
            Definition.SpecificRunTime = dateTimeOffset;
            return new ThenableSchedulerBuilder(Definition);
        }

        public IIntervalSchedulerBuilder Schedule() => new IntervalSchedulerBuilder(Definition);
    }

    private sealed class BaselineLogger<T> : IEverTaskLogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter) { }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class BaselineBlacklist : IWorkerBlacklist
    {
        public void Add(Guid guid) { }
        public bool IsBlacklisted(Guid guid) => false;
        public void Remove(Guid guid) { }
    }

    private sealed class BaselineQueueManager : IWorkerQueueManager
    {
        public IWorkerQueue GetQueue(string name) => throw new InvalidOperationException(name);

        public bool TryGetQueue(string name, out IWorkerQueue? queue)
        {
            queue = null;
            return false;
        }

        public Task<bool> TryEnqueue(string? queueName, TaskHandlerExecutor task,
                                     CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task EnqueueBlocking(string? queueName, TaskHandlerExecutor task,
                                    CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<EnqueueResult> TryEnqueueImmediate(string? queueName, TaskHandlerExecutor task,
                                                       CancellationToken cancellationToken = default) =>
            Task.FromResult(EnqueueResult.QueueFull);

        public IEnumerable<(string Name, IWorkerQueue Queue)> GetAllQueues() => [];
    }
}
