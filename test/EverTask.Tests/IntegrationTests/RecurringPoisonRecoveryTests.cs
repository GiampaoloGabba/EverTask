using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Serialization;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// B1 (P0-1 / P0-2 / P2-1) — a recurring row that is POISONED during startup recovery must be TERMINAL:
/// poisoning it via <c>SetStatus(Failed)</c> alone leaves <see cref="QueuedTask.NextRunUtc"/> set, and
/// <see cref="QueuedTask.IsRecoverable"/> revives <c>IsRecurring &amp;&amp; NextRunUtc != null &amp;&amp; Status==Failed</c>,
/// so the row comes back at every restart and is re-poisoned forever (or, if the cause healed, re-executed
/// once per restart — violating at-most-once-after-poison).
///
/// These tests drive the REAL recovery flow against the REAL <see cref="MemoryTaskStorage"/> (no mock that
/// short-circuits on Failed): the corrupt row is seeded, the real host starts (recovery runs), the same
/// storage instance is reused for a second host (a real restart), and the OBSERVED outcome is asserted —
/// <see cref="QueuedTask.NextRunUtc"/> cleared, the row no longer in <c>RetrievePending</c>, the handler
/// never executed. They FAIL on the pre-B1 code (the poison path does not clear NextRunUtc).
/// </summary>
public class RecurringPoisonRecoveryTests : IsolatedIntegrationTestBase
{
    private readonly ResilienceTestState _state = new();

    private Task<IHost> CreateMemoryHostAsync(ITaskStorage? sharedStorage, bool startHost) =>
        CreateIsolatedHostWithBuilderAsync(
            builder =>
            {
                if (sharedStorage != null)
                    builder.Services.AddSingleton(sharedStorage);
                else
                    builder.AddMemoryStorage();

                builder.Services.AddSingleton(_state);
            },
            startHost: startHost);

    /// <summary>
    /// The recurring marker index pushed by <see cref="ResilienceRecurringTaskHandler"/>.
    /// </summary>
    private const int RecurringMarker = -1;

    [Fact]
    public async Task Recurring_row_with_corrupt_metadata_is_poisoned_terminally_and_never_revived()
    {
        // Host 1: a recurring row (between runs: Completed + past NextRunUtc) whose RecurringTask metadata
        // no longer deserializes. Its Type is a real, loadable handler task — so the pre-fix code, after a
        // future heal, would happily run it as a one-shot at every restart.
        await CreateMemoryHostAsync(sharedStorage: null, startHost: false);

        var taskId = Guid.NewGuid();
        await Storage.Persist(new QueuedTask
        {
            Id              = taskId,
            Type            = new ResilienceRecurringTask().GetType().AssemblyQualifiedName!,
            Request         = EverTaskJson.Serialize(new ResilienceRecurringTask()),
            Handler         = "seeded-by-test",
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            RecurringTask   = "{ this-is-corrupt-recurring-metadata",
            NextRunUtc      = DateTimeOffset.UtcNow.AddMinutes(-1),
            CurrentRunCount = 1,
            CreatedAtUtc    = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        var sharedStorage = Storage;

        await Host!.StartAsync();

        // Real recovery poisons the row. The GATE assertion: a poisoned recurring row must have its
        // NextRunUtc cleared (otherwise it stays IsRecoverable and is resurrected). FAILS pre-B1.
        var poisoned = await TaskWaitHelper.WaitUntilAsync(
            async () => (await sharedStorage.GetAll()).First(t => t.Id == taskId),
            t => t.Status == QueuedTaskStatus.Failed,
            timeoutMs: 10000);

        poisoned.NextRunUtc.ShouldBeNull(
            "a poisoned recurring row must have NextRunUtc cleared so it is not revived at the next restart (P0-1)");
        poisoned.IsRecoverable(DateTimeOffset.UtcNow).ShouldBeFalse(
            "a terminally poisoned recurring row must no longer satisfy the recoverable predicate (P0-1)");

        // The handler must never have run (corrupt metadata is poisoned before dispatch, not demoted to one-shot).
        _state.ExecutedIndexes.ShouldNotContain(RecurringMarker);

        // Host 2 (real restart on the SAME storage): a sentinel one-shot proves recovery actually ran, then
        // the corrupt row must still be terminal and must NOT have been re-dispatched.
        var sentinelId = Guid.NewGuid();
        await sharedStorage.Persist(new QueuedTask
        {
            Id           = sentinelId,
            Type         = new ResilienceCounterTask(777).GetType().AssemblyQualifiedName!,
            Request      = EverTaskJson.Serialize(new ResilienceCounterTask(777)),
            Handler      = "seeded-by-test",
            Status       = QueuedTaskStatus.Queued,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        await CreateMemoryHostAsync(sharedStorage, startHost: true);

        await TaskWaitHelper.WaitForConditionAsync(
            () => _state.ExecutedIndexes.Contains(777), timeoutMs: 10000);

        _state.ExecutedIndexes.ShouldNotContain(RecurringMarker,
            "the poisoned recurring row must not be re-executed at the next restart (P0-1)");

        var afterRestart = (await sharedStorage.GetAll()).First(t => t.Id == taskId);
        afterRestart.Status.ShouldBe(QueuedTaskStatus.Failed);
        afterRestart.NextRunUtc.ShouldBeNull();
    }

    [Fact]
    public async Task Recurring_row_with_missing_metadata_is_poisoned_not_silently_run_as_one_shot()
    {
        // P0-2: a row flagged IsRecurring=true with a valid Request but NO RecurringTask metadata (null).
        // The pre-fix guard required !IsNullOrEmpty(RecurringTask), so it did not poison; task != null, so
        // the row was dispatched as a one-shot and (isRecovery skips UpdateTask, NextRunUtc preserved) it
        // re-ran once per restart forever. The widened guard must poison it terminally instead.
        await CreateMemoryHostAsync(sharedStorage: null, startHost: false);

        var taskId = Guid.NewGuid();
        await Storage.Persist(new QueuedTask
        {
            Id              = taskId,
            Type            = new ResilienceCounterTask(55).GetType().AssemblyQualifiedName!,
            Request         = EverTaskJson.Serialize(new ResilienceCounterTask(55)),
            Handler         = "seeded-by-test",
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            RecurringTask   = null, // missing metadata: cannot be reconstructed as a recurring schedule
            NextRunUtc      = DateTimeOffset.UtcNow.AddMinutes(-1),
            CurrentRunCount = 1,
            CreatedAtUtc    = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        var sharedStorage = Storage;

        await Host!.StartAsync();

        var poisoned = await TaskWaitHelper.WaitUntilAsync(
            async () => (await sharedStorage.GetAll()).First(t => t.Id == taskId),
            t => t.Status == QueuedTaskStatus.Failed,
            timeoutMs: 10000);

        poisoned.NextRunUtc.ShouldBeNull(
            "a recurring row with missing metadata must be poisoned terminally (NextRunUtc cleared), not demoted to a one-shot (P0-2)");
        _state.ExecutedIndexes.ShouldNotContain(55,
            "a recurring row with missing metadata must NOT be silently executed as a one-shot (P0-2)");
    }
}
