using EverTask.Logger;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests;

/// <summary>
/// #38: the poison writes of the recovery are BEST EFFORT — <c>SetRecurringTaskPoisoned</c> and
/// <c>SetStatus</c> log their own failed write and return on every relational provider — so the summary must
/// report what the ROW says, never that the call came back. Counting a swallowed write as a terminalization
/// told an operator that a restart had made progress on a row that was still recoverable and about to repeat
/// the same cycle at the next one.
/// </summary>
/// <remarks>
/// The store is a REAL <see cref="MemoryTaskStorage"/> behind <see cref="FaultInjectingTaskStorage"/>: the
/// recovery page, the predicates, the poison write and the read-back all execute for real, and the only thing
/// the test decides is whether the poison reaches the store — swallowed (the relational shape) or thrown (the
/// shape a custom storage inheriting the interface default has).
/// </remarks>
public class RecoveryPoisonOutcomeTests
{
    private const int RecurringMetadataPoisonedEvent = 1121;
    private const int RecoverySummaryWithFailuresEvent = 1117;
    private const int PoisonNotAppliedEvent = 1131;
    private const int PoisonFailedEvent = 1132;

    private readonly MemoryTaskStorage _real = new(new Mock<IEverTaskLogger<MemoryTaskStorage>>().Object);
    private readonly RecordingLogger<WorkerService> _logger = new();

    /// <summary>A recurring row whose definition is gone: the unconditional terminal poison (CU3/L44).</summary>
    private static QueuedTask RecurringWithoutDefinition() => new()
    {
        Id           = Guid.NewGuid(),
        Type         = typeof(RecoveryFailProbeTask).AssemblyQualifiedName!,
        Request      = "{}",
        Handler      = "probe",
        Status       = QueuedTaskStatus.Queued,
        IsRecurring  = true,
        RecurringTask = null,
        CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
        NextRunUtc   = DateTimeOffset.UtcNow.AddMinutes(-4)
    };

    private async Task<QueuedTask> ReadBack(Guid id) => (await _real.Get(t => t.Id == id)).Single();

    [Fact]
    public async Task Should_not_report_a_poison_the_storage_silently_dropped()
    {
        var row = RecurringWithoutDefinition();
        await _real.Persist(row);

        var storage = new FaultInjectingTaskStorage(_real);
        storage.SwallowNext(nameof(ITaskStorage.SetRecurringTaskPoisoned), times: 5);

        var service = RecoveryHarness.CreateRecoveryService(storage, logger: _logger);

        await service.ProcessPendingAsync();

        var afterFirst = await ReadBack(row.Id);
        afterFirst.Status.ShouldBe(QueuedTaskStatus.Queued, "the swallowed write left the row exactly as it was");
        afterFirst.NextRunUtc.ShouldNotBeNull("a row with its cursor still set is back in the next recovery page");

        _logger.Count(RecurringMetadataPoisonedEvent).ShouldBe(0,
            "a terminalization that never happened must not be announced");
        _logger.Count(PoisonNotAppliedEvent).ShouldBe(1,
            "the operator must be told the poison did not land, not that it did");
        _logger.Messages.ShouldContain(m => m.Contains("0 marked Failed"),
            "the summary counts a swallowed poison as the transient failure it is");
        _logger.Messages.ShouldContain(m => m.Contains("1 failed (still recoverable"),
            "the row is still recoverable, which is what the summary exists to say");

        // And it really is: the next restart meets the same row and tries to poison it again.
        await service.ProcessPendingAsync();
        storage.Calls[nameof(ITaskStorage.SetRecurringTaskPoisoned)].ShouldBe(2);
    }

    [Fact]
    public async Task Should_report_the_poison_when_the_write_really_lands()
    {
        var row = RecurringWithoutDefinition();
        await _real.Persist(row);

        var storage = new FaultInjectingTaskStorage(_real);
        var service = RecoveryHarness.CreateRecoveryService(storage, logger: _logger);

        await service.ProcessPendingAsync();

        var poisoned = await ReadBack(row.Id);
        poisoned.Status.ShouldBe(QueuedTaskStatus.Failed);
        poisoned.NextRunUtc.ShouldBeNull("the poison of a recurring row clears the cursor in the same write (P0-1)");

        _logger.Count(RecurringMetadataPoisonedEvent).ShouldBe(1,
            "a poison that landed is reported exactly as before");
        _logger.Count(PoisonNotAppliedEvent).ShouldBe(0);
        _logger.Messages.ShouldContain(m => m.Contains("1 marked Failed"),
            "the confirmation must not cost a real terminalization its place in the summary");
    }

    [Fact]
    public async Task Should_keep_recovering_when_the_poison_write_throws()
    {
        // A custom storage inherits the interface's non-swallowing default, so a poison CAN throw. Letting it
        // out aborts the wave over one unusable row and leaves every sibling of its page unrecovered.
        var row = RecurringWithoutDefinition();
        await _real.Persist(row);

        var storage = new FaultInjectingTaskStorage(_real);
        storage.FailAlways(nameof(ITaskStorage.SetRecurringTaskPoisoned));

        var service = RecoveryHarness.CreateRecoveryService(storage, logger: _logger);

        await Should.NotThrowAsync(() => service.ProcessPendingAsync());

        (await ReadBack(row.Id)).Status.ShouldBe(QueuedTaskStatus.Queued);
        _logger.Count(PoisonFailedEvent).ShouldBe(1, "the failed write is reported for what it is");
        _logger.Count(RecurringMetadataPoisonedEvent).ShouldBe(0);
        _logger.Count(RecoverySummaryWithFailuresEvent).ShouldBe(1,
            "the recovery must reach its summary instead of dying on one row");
        _logger.Messages.ShouldContain(m => m.Contains("0 marked Failed"));
    }

    [Fact]
    public async Task Should_not_report_a_swallowed_poison_of_a_one_shot_either()
    {
        // The one-shot half of the same helper: its poison is SetStatus(Failed), best effort on every
        // relational provider in exactly the same way.
        var row = new QueuedTask
        {
            Id           = Guid.NewGuid(),
            Type         = "EverTask.Tests.TypeThatNoLongerExists, EverTask.Tests.GoneAssembly",
            Request      = "{}",
            Handler      = "probe",
            Status       = QueuedTaskStatus.Queued,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10)
        };
        await _real.Persist(row);

        var storage = new FaultInjectingTaskStorage(_real);
        storage.SwallowNext(nameof(ITaskStorage.SetStatus), times: 5);

        var service = RecoveryHarness.CreateRecoveryService(storage, logger: _logger);

        await service.ProcessPendingAsync();

        (await ReadBack(row.Id)).Status.ShouldBe(QueuedTaskStatus.Queued,
            "the row a swallowed SetStatus left behind is still recoverable");
        _logger.Count(PoisonNotAppliedEvent).ShouldBe(1);
        _logger.Messages.ShouldContain(m => m.Contains("0 marked Failed"),
            "an unloadable type is a permanent cause, but nothing was written: the row comes back");
    }
}
