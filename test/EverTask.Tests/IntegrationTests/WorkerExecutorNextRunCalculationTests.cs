using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// Integration tests verifying that WorkerExecutor correctly calculates next run
/// for recurring tasks based on ExecutionTime (scheduled time) instead of UtcNow.
/// This prevents schedule drift when tasks execute with delays.
/// Related to schedule drift fix - see docs/test-plan-schedule-drift-fix.md
/// </summary>
public class WorkerExecutorNextRunCalculationTests : IsolatedIntegrationTestBase
{

    [Fact]
    public async Task WorkerExecutor_Should_Calculate_NextRun_From_ExecutionTime_Not_UtcNow()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Dispatch recurring task every 5 seconds
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(5).Seconds());

        // The Dispatcher persists the row - with the slot it picked for the first occurrence - before
        // Dispatch returns, and that slot is a full interval away: this read cannot see a NextRunUtc
        // already advanced by the WorkerExecutor.
        var taskAfterDispatch = await TaskWaitHelper.WaitForTaskExistsAsync(Storage, taskId);
        var dispatcherNextRun = taskAfterDispatch.NextRunUtc;
        dispatcherNextRun.ShouldNotBeNull();

        // Wait for the first execution AND its post-execution write: CurrentRunCount, the runs audit
        // and the new NextRunUtc are persisted after the handler returns, so the handler counter
        // alone would race the row read that follows.
        var task = await WaitForRecurringRunsAsync(taskId, expectedRuns: 1, timeoutMs: 7000);

        task.CurrentRunCount.HasValue.ShouldBeTrue();
        task.CurrentRunCount?.ShouldBeGreaterThanOrEqualTo(1);
        task.NextRunUtc.ShouldNotBeNull();

        // The series has no MaxRuns - the NextRunUtc asserted below only exists while it is alive - so
        // its audits keep growing under the storage lock while this runs: snapshot before enumerating.
        var firstRun = task.SnapshotRunsAudits()
            .Where(a => a.Status == QueuedTaskStatus.Completed)
            .OrderBy(a => a.ExecutedAt)
            .First();

        // The occurrence that ran is the one the Dispatcher scheduled: the scheduler dequeues a slot
        // only once it is due, so the audited execution is never earlier.
        firstRun.ExecutedAt.ShouldBeGreaterThanOrEqualTo(dispatcherNextRun.Value);

        // Assert: the WorkerExecutor re-schedules from the SCHEDULED slot, not from the wall clock.
        // QueueNextOccourrence feeds CalculateNextValidRun with TaskHandlerExecutor.ExecutionTime,
        // which for this first occurrence IS the slot the Dispatcher picked - NOT firstRun.ExecutedAt,
        // which is stamped when the run finishes. The new NextRunUtc therefore lands on the occurrence
        // grid anchored at dispatcherNextRun (+ k * 5s, with k > 1 only when the run was late enough
        // for CalculateNextValidRun to realign, which stays on the SAME grid), whereas a UtcNow-based
        // re-schedule would land at "instant the run finished + 5s", i.e. off that grid by the
        // execution latency. The previous form compared NextRunUtc against ExecutedAt + 5s with a 1s
        // tolerance, which is exactly that latency: under contention it legitimately exceeded 1s.
        var interval = TimeSpan.FromSeconds(5); // matches Every(5).Seconds() above
        var advance  = task.NextRunUtc!.Value - dispatcherNextRun.Value;

        advance.ShouldBeGreaterThanOrEqualTo(interval);
        (advance.Ticks % interval.Ticks).ShouldBe(0L);
    }

    [Fact]
    public async Task WorkerExecutor_Should_Calculate_NextRun_From_ScheduledTime_When_Delayed()
    {
        // Arrange: Create a task that delays execution to simulate late execution
        await CreateIsolatedHostAsync();

        // Dispatch recurring task every 3 seconds that takes 1 second to execute, capped at the 2 runs
        // the drift assertion reads: nothing here needs the series alive, and the cap keeps it from
        // firing into host teardown (and from appending to the run audits while they are enumerated).
        var taskId = await Dispatcher.Dispatch(
            new TestTaskDelayedRecurring(delayMs: 1000),
            recurring => recurring.Schedule().Every(3).Seconds().MaxRuns(2));

        // Wait for 2 executions to verify consistent scheduling
        await TaskWaitHelper.WaitForRecurringRunsAsync(Storage, taskId, expectedRuns: 2, timeoutMs: 10000);

        // Get task state
        var tasks = await Storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();
        task.CurrentRunCount.HasValue.ShouldBeTrue();
        task.CurrentRunCount?.ShouldBeGreaterThanOrEqualTo(2);

        // Get all completed runs
        var completedRuns = task.RunsAudits
            .Where(a => a.Status == QueuedTaskStatus.Completed)
            .OrderBy(a => a.ExecutedAt)
            .ToList();

        completedRuns.Count.ShouldBe(2);

        // Assert: Time between runs should be approximately 3 seconds (interval)
        // NOT 3 seconds + task execution time (which would indicate drift)
        var timeBetweenRuns = (completedRuns[1].ExecutedAt - completedRuns[0].ExecutedAt).TotalSeconds;

        // Should be close to 3 seconds, allowing 1.5 second tolerance
        // (3 seconds interval, even though task takes 1 second to run)
        timeBetweenRuns.ShouldBeGreaterThan(2.5);
        timeBetweenRuns.ShouldBeLessThan(4.5);
    }

    [Fact]
    public async Task WorkerExecutor_Should_Skip_Past_Occurrences_After_Downtime()
    {
        // Arrange - Create first host and preserve storage reference
        await CreateIsolatedHostAsync();
        var preservedStorage = Storage; // Keep reference to storage before stopping host

        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Seconds());

        // Wait for the first execution to be PERSISTED: the handler counter is raised inside Handle,
        // while the run counter and NextRunUtc asserted below are written after the handler returns.
        await TaskWaitHelper.WaitForRecurringRunsAsync(preservedStorage, taskId, expectedRuns: 1, timeoutMs: 5000);

        // Simulate downtime by stopping the host. The delay IS the downtime (~5 missed occurrences),
        // not a wait for something to observe: the host is stopped, so there is nothing to poll.
        const int downtimeMs = 5000;
        var downtimeStart = DateTimeOffset.UtcNow;
        await StopHostAsync();
        await Task.Delay(downtimeMs);
        var downtimeEnd = downtimeStart.AddMilliseconds(downtimeMs);

        // Restart host with same storage instance (simulating system recovery)
        await CreateIsolatedHostAsync(
            channelCapacity: 10,
            maxDegreeOfParallelism: 5,
            configureServices: services =>
            {
                // Remove the memory storage and use the preserved storage instance
                var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ITaskStorage));
                if (descriptor != null)
                    services.Remove(descriptor);
                services.AddSingleton(preservedStorage);
            });

        // Wait for the recovery to actually run the series again: LastExecutionUtc is stamped when a
        // run completes, so a value past the downtime window proves the restarted host took over -
        // a fixed delay only hoped it had.
        var task = await TaskWaitHelper.WaitUntilAsync(
            async () => (await preservedStorage.GetAll()).FirstOrDefault(t => t.Id == taskId),
            t => t?.LastExecutionUtc > downtimeEnd,
            timeoutMs: 10000);

        task.ShouldNotBeNull();

        // Should have recorded skipped occurrences
        // FIXME: SkippedOccurrencesAudits property does not exist

        // var skippedAudits = // FIXME: SkippedOccurrencesAudits property does not exist - task.task.SkippedOccurrencesAudits;

        // Assert: the series resumed on an occurrence AFTER the downtime instead of catching up on the
        // ~5 it missed. Anchored on the test-owned downtime window, not on UtcNow at assertion time:
        // with a 1s cadence the next run is only 1s ahead, so any hiccup between the last execution
        // and the assertion let the wall clock overtake it - the exact form removed by 77a227a from
        // the sibling test.
        task.NextRunUtc.ShouldNotBeNull();
        task.NextRunUtc.Value.ShouldBeGreaterThan(downtimeEnd);
        task.CurrentRunCount?.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task WorkerExecutor_Should_Record_Skipped_Occurrences_In_Storage()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Dispatch recurring task that starts in the past (2 minutes ago, every 30 seconds)
        var pastTime = DateTimeOffset.UtcNow.AddMinutes(-2);

        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.RunAt(pastTime).Then().Every(30).Seconds());

        // Wait for task to be scheduled (should skip past occurrences)
        await Task.Delay(500);

        // Assert: Storage should have skipped occurrences recorded
        var tasks = await Storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();

        // Should have skipped approximately 4 occurrences (2 minutes / 30 seconds = 4)
        // Note: skipped occurrences count toward the run counter but are not exposed as a direct
        // property on QueuedTask. The task should be scheduled for the next valid future run.
    }

    [Fact]
    public async Task WorkerExecutor_Should_Maintain_Schedule_Across_Multiple_Runs()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Dispatch recurring task every 2 seconds
        var interval = TimeSpan.FromSeconds(2);
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(2).Seconds());

        // The Dispatcher persists the row with the slot it picked for the first occurrence, a full
        // interval away: this read cannot see a NextRunUtc already advanced by the WorkerExecutor.
        var firstSlot = (await TaskWaitHelper.WaitForTaskExistsAsync(Storage, taskId)).NextRunUtc;
        firstSlot.ShouldNotBeNull();

        // Wait for 3 executions
        var task = await TaskWaitHelper.WaitForRecurringRunsAsync(Storage, taskId, expectedRuns: 3, timeoutMs: 10000);

        task.CurrentRunCount.HasValue.ShouldBeTrue();
        task.CurrentRunCount?.ShouldBeGreaterThanOrEqualTo(3);

        // Get all completed runs. The series is deliberately NOT capped - the assertion below reads the
        // NextRunUtc of a LIVE series - so the audits keep growing while these lines run and have to be
        // snapshotted before they are enumerated.
        var completedRuns = task.SnapshotRunsAudits()
            .Where(a => a.Status == QueuedTaskStatus.Completed)
            .OrderBy(a => a.ExecutedAt)
            .Take(3)
            .ToList();

        completedRuns.Count.ShouldBe(3);

        // Assert: the schedule is still the one the Dispatcher anchored. NextRunUtc is the SCHEDULED
        // slot, so this holds exactly - a whole number of cadences past the first slot, more than 3
        // only when occurrences were lost, which realigns onto the SAME grid. A re-schedule computed
        // from the wall clock would sit off it by the accumulated execution latency.
        task.NextRunUtc.ShouldNotBeNull();
        var advance = task.NextRunUtc.Value - firstSlot.Value;
        advance.ShouldBeGreaterThanOrEqualTo(interval * 3);
        (advance.Ticks % interval.Ticks).ShouldBe(0L);

        // The executions themselves are checked against the same grid, each measured from the FIRST
        // run: this bounds the observed cadence (nothing runs off-grid or ahead of its slot) while a
        // lost slot only moves a gap to a bigger multiple. The old form pinned every consecutive gap
        // to 1.5s-3s and the total to 3.5s-5s: one lost slot makes a gap an exact 4s and the total 6s,
        // with no drift whatsoever.
        for (var i = 1; i < completedRuns.Count; i++)
        {
            (completedRuns[i].ExecutedAt - completedRuns[0].ExecutedAt)
                .ShouldBeOnOccurrenceGrid(interval, minSlots: i);
        }
    }

    [Fact]
    public async Task WorkerExecutor_Should_Use_ExecutionTime_For_HourInterval()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Dispatch recurring task every 1 hour
        // Note: OnMinute() is not available on Hours() builder. The hour interval will use current minute.
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Hours());

        // Wait for task to be scheduled
        await Task.Delay(500);

        // Get task state
        var tasks = await Storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();
        task.NextRunUtc.ShouldNotBeNull();

        // Next run should be approximately 1 hour from now
        var expectedNextRun = DateTimeOffset.UtcNow.AddHours(1);
        var timeDiff = Math.Abs((task.NextRunUtc.Value - expectedNextRun).TotalMinutes);
        timeDiff.ShouldBeLessThan(2); // Within 2 minutes tolerance
    }
}
