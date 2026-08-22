using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.IntegrationTests;

[Collection("TimingSensitiveTests")]
public class WorkerServiceScheduledIntegrationTests : IsolatedIntegrationTestBase
{
    [Fact]
    public async Task Should_execute_delayed_task()
    {
        await CreateIsolatedHostAsync();

        var task = new TestTaskConcurrent1();
        var taskId = await Dispatcher.Dispatch(task, TimeSpan.FromSeconds(1.2));

        // Wait for task to be in waiting queue
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.WaitingQueue, timeoutMs: 2000);

        var pt = await Storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].Status.ShouldBe(QueuedTaskStatus.WaitingQueue);

        // Wait for task to complete after delay
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 2000);
        pt = await Storage.RetrievePending(null, null, 10);
        pt.Length.ShouldBe(0);

        var tasks = await Storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldBeNull();
    }

    [Fact]
    public async Task Should_execute_specific_time_task()
    {
        await CreateIsolatedHostAsync();

        var task = new TestTaskDelayed1();
        var specificDate = DateTimeOffset.Now.AddSeconds(1.2);
        var taskId = await Dispatcher.Dispatch(task, specificDate);

        // Wait for task to be in waiting queue
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.WaitingQueue, timeoutMs: 2000);

        var pt = await Storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].Status.ShouldBe(QueuedTaskStatus.WaitingQueue);

        // Wait for task to complete after scheduled time
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 2000);
        pt = await Storage.RetrievePending(null, null, 10);
        pt.Length.ShouldBe(0);

        var tasks = await Storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldBeNull();
    }

    [Fact]
    public async Task Should_execute_recurring_cron()
    {
        await CreateIsolatedHostAsync();

        var task = new TestTaskDelayed2();

        // Adaptive parameters: tighter constraints locally, more generous on CI
        var cronInterval = TestEnvironment.GetCronInterval(localSeconds: 7, ciSeconds: 10);
        var maxRuns = TestEnvironment.GetIterations(local: 3, ci: 2);
        var timeout = TestEnvironment.GetTimeout(localMs: 45000, ciMs: 65000);

        // Test RunDelayed + Cron combination
        // Local: */5 (every 5s), 3 runs, 25s timeout
        // CI: */10 (every 10s), 2 runs, 50s timeout (generous for slow CI with coverage + cron jitter)
        var taskId = await Dispatcher.Dispatch(task, builder => builder
            .RunDelayed(TimeSpan.FromMilliseconds(1500))
            .Then()
            .UseCron($"*/{cronInterval} * * * * *")
            .MaxRuns(maxRuns));

        // Wait for task to be scheduled
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.WaitingQueue, timeoutMs: 2000);

        var pt = await Storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].Status.ShouldBe(QueuedTaskStatus.WaitingQueue);

        // Wait for recurring task to complete all runs
        var completedTask = await WaitForRecurringRunsAsync(taskId, expectedRuns: maxRuns, timeoutMs: timeout);

        // Use the returned task from WaitForRecurringRunsAsync to avoid race conditions
        completedTask.CurrentRunCount.ShouldBe(maxRuns);
        completedTask.RunsAudits.Count.ShouldBe(maxRuns);

        // Verify in storage as well
        pt = await Storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].CurrentRunCount.ShouldBe(maxRuns);
        pt[0].RunsAudits.Count.ShouldBe(maxRuns);

        // Counter already verified via RunsAudits above - no need for static counter check
    }

    [Fact]
    public async Task Should_execute_recurring_task_with_second_interval()
    {
        await CreateIsolatedHostAsync();

        var task = new TestTaskRecurringSeconds();

        // Every 2 seconds, max 3 runs
        var taskId = await Dispatcher.Dispatch(task, builder => builder.Schedule().Every(2).Seconds().MaxRuns(3));

        // Wait for task to be parked: the first occurrence is 2s away, so WaitingQueue is observable
        // for that whole window (timeout sized on the window, not on a 1s default)
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.WaitingQueue, timeoutMs: 2000);

        var pt = await Storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].Status.ShouldBe(QueuedTaskStatus.WaitingQueue);
        pt[0].IsRecurring.ShouldBeTrue();

        // Wait for recurring task to complete 3 runs
        await WaitForRecurringRunsAsync(taskId, expectedRuns: 3, timeoutMs: 10000);

        pt = await Storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].CurrentRunCount.ShouldBe(3);
        pt[0].RunsAudits.Count.ShouldBe(3);

        // Verify all runs completed successfully
        pt[0].RunsAudits.All(r => r != null && r.Status == QueuedTaskStatus.Completed).ShouldBeTrue();
        pt[0].RunsAudits.All(r => r != null && r.Exception == null).ShouldBeTrue();
    }

    [Fact]
    public async Task Should_execute_recurring_task_with_initial_delay_then_interval()
    {
        await CreateIsolatedHostAsync();

        var task = new TestTaskRecurringSeconds();

        // Wait 500ms, then every 2 seconds, max 3 runs
        var taskId = await Dispatcher.Dispatch(task, builder =>
            builder.RunDelayed(TimeSpan.FromMilliseconds(500))
                   .Then()
                   .Every(2).Seconds()
                   .MaxRuns(3));

        // RunDelayed(500ms): WaitingQueue is observable only for that 500ms window, and the row never
        // returns to it once the occurrence is delivered - a first poll landing after the window made
        // the wait run out its whole timeout with nothing left to observe. Wait for the row to be
        // accepted by the pipeline instead.
        await WaitForTaskAcceptedAsync(taskId, timeoutMs: 2000);

        var pt = await Storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].Status.ShouldBeOneOf(QueuedTaskStatus.WaitingQueue, QueuedTaskStatus.Queued,
                                   QueuedTaskStatus.InProgress, QueuedTaskStatus.Completed);
        pt[0].IsRecurring.ShouldBeTrue();

        // Wait for recurring task to complete 3 runs
        await WaitForRecurringRunsAsync(taskId, expectedRuns: 3, timeoutMs: 10000);

        pt = await Storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].CurrentRunCount.ShouldBe(3);
        pt[0].RunsAudits.Count.ShouldBe(3);

        // Verify all runs completed successfully
        pt[0].RunsAudits.All(r => r != null && r.Status == QueuedTaskStatus.Completed).ShouldBeTrue();
    }

    [Fact]
    public async Task Should_execute_recurring_task_with_run_now_then_interval()
    {
        await CreateIsolatedHostAsync();

        var task = new TestTaskRecurringSeconds();

        // Run immediately, then every 2 seconds, max 3 runs
        var taskId = await Dispatcher.Dispatch(task, builder =>
            builder.RunNow()
                   .Then()
                   .Every(2).Seconds()
                   .MaxRuns(3));

        // RunNow: the first occurrence is due immediately, so the task can leave WaitingQueue before
        // the first poll and never come back to it. Wait for it to be accepted by the pipeline instead.
        await WaitForTaskAcceptedAsync(taskId, timeoutMs: 2000);

        var pt = await Storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].IsRecurring.ShouldBeTrue();

        // Wait for recurring task to complete 3 runs
        await WaitForRecurringRunsAsync(taskId, expectedRuns: 3, timeoutMs: 10000);

        pt = await Storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].CurrentRunCount.ShouldBe(3);
        pt[0].RunsAudits.Count.ShouldBe(3);

        // Verify all runs completed successfully
        pt[0].RunsAudits.All(r => r != null && r.Status == QueuedTaskStatus.Completed).ShouldBeTrue();
    }

    [Fact]
    public async Task Should_reschedule_recurring_task_after_failure_with_retry()
    {
        await CreateIsolatedHostAsync();

        // Fail the first 2 attempts, succeed on the 3rd
        var task = new TestTaskRecurringWithFailure(FailUntilCount: 2);

        // Every 2 seconds, max 3 runs - first run will retry internally due to LinearRetryPolicy(3, 50ms)
        var taskId = await Dispatcher.Dispatch(task, builder => builder.Schedule().Every(2).Seconds().MaxRuns(3));

        // Wait for task to be parked: the first occurrence is 2s away (timeout sized on that window)
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.WaitingQueue, timeoutMs: 2000);

        // Wait for recurring task to complete 3 runs
        // First run: fails twice (retry), succeeds on 3rd attempt
        // Second run: succeeds immediately (counter=4, > threshold)
        // Third run: succeeds immediately (counter=5, > threshold)
        await WaitForRecurringRunsAsync(taskId, expectedRuns: 3, timeoutMs: 15000);

        var pt = await Storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].CurrentRunCount.ShouldBe(3);
        pt[0].RunsAudits.Count.ShouldBe(3);

        // Verify all 3 recurring runs completed successfully (retries are internal to each run)
        pt[0].RunsAudits.All(r => r != null && r.Status == QueuedTaskStatus.Completed).ShouldBeTrue();
    }

    [Fact]
    public async Task Should_handle_multiple_concurrent_recurring_tasks()
    {
        await CreateIsolatedHostAsync();

        // Reset counters

        // Dispatch 3 different recurring tasks with different intervals
        var task1 = new TestTaskRecurringSeconds();
        var task1Id = await Dispatcher.Dispatch(task1, builder => builder.Schedule().Every(2).Seconds().MaxRuns(3));

        var task2 = new TestTaskDelayed1();
        var task2Id = await Dispatcher.Dispatch(task2, builder => builder.Schedule().Every(3).Seconds().MaxRuns(2));

        var task3 = new TestTaskDelayed2();
        var task3Id = await Dispatcher.Dispatch(task3, builder => builder.RunNow().Then().Every(2).Seconds().MaxRuns(2));

        // Wait for all tasks to be scheduled. task1/task2 have their first occurrence 2s/3s away, so
        // they are observably parked in WaitingQueue for that window. task3 is a RunNow: its
        // occurrence is due immediately and the scheduler can flip it to Queued before the first poll
        // (after which a recurring run ends in Completed and never returns to WaitingQueue), so
        // waiting for WaitingQueue there is a race — wait for it to be accepted by the pipeline.
        await WaitForTaskStatusAsync(task1Id, QueuedTaskStatus.WaitingQueue, timeoutMs: 2000);
        await WaitForTaskStatusAsync(task2Id, QueuedTaskStatus.WaitingQueue, timeoutMs: 2000);
        await WaitForTaskAcceptedAsync(task3Id, timeoutMs: 2000);

        // Verify all 3 tasks are in storage
        var allTasks = await Storage.GetAll();
        allTasks.Length.ShouldBe(3);
        allTasks.All(t => t.IsRecurring).ShouldBeTrue();

        // Wait for all tasks to complete their runs
        await WaitForRecurringRunsAsync(task1Id, expectedRuns: 3, timeoutMs: 15000);
        await WaitForRecurringRunsAsync(task2Id, expectedRuns: 2, timeoutMs: 15000);
        await WaitForRecurringRunsAsync(task3Id, expectedRuns: 2, timeoutMs: 15000);

        // Verify each task completed the correct number of runs independently
        allTasks = await Storage.GetAll();
        allTasks.Length.ShouldBe(3);

        var completedTask1 = allTasks.FirstOrDefault(t => t.Id == task1Id);
        var completedTask2 = allTasks.FirstOrDefault(t => t.Id == task2Id);
        var completedTask3 = allTasks.FirstOrDefault(t => t.Id == task3Id);

        completedTask1.ShouldNotBeNull();
        completedTask1.CurrentRunCount.ShouldBe(3);
        completedTask1.RunsAudits.Count.ShouldBe(3);
        completedTask1.RunsAudits.All(r => r != null && r.Status == QueuedTaskStatus.Completed).ShouldBeTrue();

        completedTask2.ShouldNotBeNull();
        completedTask2.CurrentRunCount.ShouldBe(2);
        completedTask2.RunsAudits.Count.ShouldBe(2);
        completedTask2.RunsAudits.All(r => r != null && r.Status == QueuedTaskStatus.Completed).ShouldBeTrue();

        completedTask3.ShouldNotBeNull();
        completedTask3.CurrentRunCount.ShouldBe(2);
        completedTask3.RunsAudits.Count.ShouldBe(2);
        completedTask3.RunsAudits.All(r => r != null && r.Status == QueuedTaskStatus.Completed).ShouldBeTrue();

        // Verify no interference - total completed runs should match expected
        var totalCompletedRuns = allTasks.Sum(t => t.CurrentRunCount ?? 0);
        totalCompletedRuns.ShouldBe(7); // 3 + 2 + 2
    }
}
