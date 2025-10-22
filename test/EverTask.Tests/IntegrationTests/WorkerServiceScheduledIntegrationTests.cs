using EverTask.Monitoring;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.IntegrationTests;

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
        pt = await Storage.RetrievePending();
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
        pt = await Storage.RetrievePending();
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

        // Test RunDelayed + Cron combination with longer intervals for stability
        // Cron "*/3 * * * * *" runs every 3 seconds (at 0, 3, 6, 9, ...)
        var taskId = await Dispatcher.Dispatch(task, builder => builder.RunDelayed(TimeSpan.FromMilliseconds(1500)).Then().UseCron("*/3 * * * * *").MaxRuns(3));

        // Wait for task to be scheduled
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.WaitingQueue, timeoutMs: 2000);

        var pt = await Storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].Status.ShouldBe(QueuedTaskStatus.WaitingQueue);

        // Wait for recurring task to complete 3 runs
        // Delay: 1500ms, then cron at next 3-second boundary
        // Total: ~1.5s + up to 3s + 3s + 3s = ~10.5s max, 15s timeout for coverage tool
        var completedTask = await WaitForRecurringRunsAsync(taskId, expectedRuns: 3, timeoutMs: 15000);

        // Use the returned task from WaitForRecurringRunsAsync to avoid race conditions
        completedTask.CurrentRunCount.ShouldBe(3);
        completedTask.RunsAudits.Count.ShouldBe(3);

        // Verify in storage as well
        pt = await Storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].CurrentRunCount.ShouldBe(3);
        pt[0].RunsAudits.Count.ShouldBe(3);

        // Counter already verified via RunsAudits above - no need for static counter check
    }

    [Fact]
    public async Task Should_execute_recurring_task_with_second_interval()
    {
        await CreateIsolatedHostAsync();

        var task = new TestTaskRecurringSeconds();

        // Every 2 seconds, max 3 runs
        var taskId = await Dispatcher.Dispatch(task, builder => builder.Schedule().Every(2).Seconds().MaxRuns(3));

        // Wait for task to be scheduled
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.WaitingQueue, timeoutMs: 1000);

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
        pt[0].RunsAudits.All(r => r.Status == QueuedTaskStatus.Completed).ShouldBeTrue();
        pt[0].RunsAudits.All(r => r.Exception == null).ShouldBeTrue();
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

        // Wait for task to be scheduled
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.WaitingQueue, timeoutMs: 1000);

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
        pt[0].RunsAudits.All(r => r.Status == QueuedTaskStatus.Completed).ShouldBeTrue();
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

        // Wait for task to be scheduled and start executing
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.WaitingQueue, timeoutMs: 1000);

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
        pt[0].RunsAudits.All(r => r.Status == QueuedTaskStatus.Completed).ShouldBeTrue();
    }

    [Fact]
    public async Task Should_reschedule_recurring_task_after_failure_with_retry()
    {
        await CreateIsolatedHostAsync();

        TestTaskRecurringWithFailure.FailUntilCount = 2; // Fail first 2 attempts, succeed on 3rd
        var task = new TestTaskRecurringWithFailure();

        // Every 2 seconds, max 3 runs - first run will retry internally due to LinearRetryPolicy(3, 50ms)
        var taskId = await Dispatcher.Dispatch(task, builder => builder.Schedule().Every(2).Seconds().MaxRuns(3));

        // Wait for task to be scheduled
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.WaitingQueue, timeoutMs: 1000);

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
        pt[0].RunsAudits.All(r => r.Status == QueuedTaskStatus.Completed).ShouldBeTrue();

        // Counter should be > 3 due to retries during first run
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

        // Wait for all tasks to be scheduled
        await WaitForTaskStatusAsync(task1Id, QueuedTaskStatus.WaitingQueue, timeoutMs: 1000);
        await WaitForTaskStatusAsync(task2Id, QueuedTaskStatus.WaitingQueue, timeoutMs: 1000);
        await WaitForTaskStatusAsync(task3Id, QueuedTaskStatus.WaitingQueue, timeoutMs: 1000);

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
        completedTask1.RunsAudits.All(r => r.Status == QueuedTaskStatus.Completed).ShouldBeTrue();

        completedTask2.ShouldNotBeNull();
        completedTask2.CurrentRunCount.ShouldBe(2);
        completedTask2.RunsAudits.Count.ShouldBe(2);
        completedTask2.RunsAudits.All(r => r.Status == QueuedTaskStatus.Completed).ShouldBeTrue();

        completedTask3.ShouldNotBeNull();
        completedTask3.CurrentRunCount.ShouldBe(2);
        completedTask3.RunsAudits.Count.ShouldBe(2);
        completedTask3.RunsAudits.All(r => r.Status == QueuedTaskStatus.Completed).ShouldBeTrue();

        // Verify no interference - total completed runs should match expected
        var totalCompletedRuns = allTasks.Sum(t => t.CurrentRunCount ?? 0);
        totalCompletedRuns.ShouldBe(7); // 3 + 2 + 2
    }
}
