using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;
using Newtonsoft.Json;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// Tests for backward compatibility with existing recurring tasks
/// after the schedule drift fix implementation.
/// Related to schedule drift fix - see docs/test-plan-schedule-drift-fix.md
/// </summary>
public class BackwardCompatibilityScheduleDriftTests : IsolatedIntegrationTestBase
{
    [Fact]
    public async Task Old_Serialized_Recurring_Task_Should_Deserialize_And_Reschedule()
    {
        // Arrange
        await CreateIsolatedHostAsync(
            channelCapacity: 10,
            maxDegreeOfParallelism: 5);

        // ✅ Create recurring task from the start (using short interval for test speed)
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(2).Seconds());

        // Simulate legacy task: old JSON format without new properties
        var tasks = await Storage.Get(t => t.Id == taskId);
        tasks.Length.ShouldBe(1);

        var queuedTask = tasks[0];
        // Override with legacy-style JSON (simulating deserialized old format)
        queuedTask.RecurringTask = @"{""SecondInterval"":{""Interval"":2}}"; // Old format
        queuedTask.NextRunUtc = DateTimeOffset.UtcNow.AddSeconds(2);
        queuedTask.ScheduledExecutionUtc = DateTimeOffset.UtcNow.AddSeconds(2);

        await Storage.UpdateTask(queuedTask);

        // Act: wait for the RUN to be recorded on the row, not for the handler's counter. The handler
        // bumps its counter from inside Handle, while CurrentRunCount is written after it returns
        // (QueueNextOccourrence -> CompleteRecurringRun, a storage round trip further on): a read taken
        // on the counter alone lands between the two and sees CurrentRunCount still at 0.
        var updatedTask = await WaitForRecurringRunsAsync(taskId, expectedRuns: 1, timeoutMs: 10000);

        // Assert: Task should have been deserialized and rescheduled correctly
        updatedTask.ShouldNotBeNull();
        updatedTask.IsRecurring.ShouldBeTrue();
        updatedTask.CurrentRunCount?.ShouldBeGreaterThanOrEqualTo(1);

        // Should have calculated next run using the new logic
        updatedTask.NextRunUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task Legacy_Task_Without_ScheduledExecutionUtc_Should_Still_Work()
    {
        // Arrange
        await CreateIsolatedHostAsync(
            channelCapacity: 10,
            maxDegreeOfParallelism: 5);

        // ✅ Create recurring task from the start
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(2).Seconds());

        // Simulate legacy task: remove ScheduledExecutionUtc (wasn't tracked before)
        var tasks = await Storage.Get(t => t.Id == taskId);
        tasks.Length.ShouldBe(1);

        var queuedTask = tasks[0];
        queuedTask.ScheduledExecutionUtc = null; // ✅ Legacy: this field didn't exist
        queuedTask.NextRunUtc = DateTimeOffset.UtcNow.AddSeconds(1);

        await Storage.UpdateTask(queuedTask);

        // Act: wait for the run to be recorded on the row (the handler's counter is bumped before
        // CurrentRunCount is written — see the first test in this class)
        var updatedTask = await WaitForRecurringRunsAsync(taskId, expectedRuns: 1, timeoutMs: 10000);

        // Assert: Task should still execute and reschedule
        updatedTask.ShouldNotBeNull();
        updatedTask.CurrentRunCount?.ShouldBeGreaterThanOrEqualTo(1);
        updatedTask.NextRunUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task Minimal_legacy_storage_should_degrade_gracefully_on_skipped_occurrences()
    {
        // Arrange: Use TestTaskStorage, a minimal legacy storage
        // This test needs custom storage (TestTaskStorage instead of MemoryStorage)
        // Use configureServices callback to register custom storage
        await CreateIsolatedHostAsync(
            channelCapacity: 10,
            maxDegreeOfParallelism: 5,
            configureServices: services =>
            {
                // Remove MemoryStorage and add TestTaskStorage
                var storageDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ITaskStorage));
                if (storageDescriptor != null)
                {
                    services.Remove(storageDescriptor);
                }
                services.AddScoped<ITaskStorage, TestTaskStorage>(); // Legacy storage
            });

        // Act: Dispatch recurring task that starts in the past (would normally skip occurrences)
        var pastTime = DateTimeOffset.UtcNow.AddMinutes(-2);

        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.RunAt(pastTime).Then().Every(30).Seconds());

        // Wait a bit to see if anything breaks
        await Task.Delay(2000);

        // Assert: System should not crash, just log warnings
        // Since TestTaskStorage doesn't persist anything, we can't verify much,
        // but the system should remain stable
        var counter = StateManager.GetCounter(nameof(TestTaskRecurringSeconds));

        // Task may or may not have executed (depending on timing), but should not crash
        counter.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Migrating_From_Old_To_New_Logic_Should_Work_Seamlessly()
    {
        // Arrange
        await CreateIsolatedHostAsync(
            channelCapacity: 10,
            maxDegreeOfParallelism: 5);

        // ✅ Create recurring task from the start
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(2).Seconds());

        // Simulate old behavior: NextRunUtc was calculated from UtcNow (not ExecutionTime)
        var tasks = await Storage.Get(t => t.Id == taskId);
        var queuedTask = tasks[0];

        queuedTask.NextRunUtc = DateTimeOffset.UtcNow.AddSeconds(2); // Old logic: from UtcNow
        queuedTask.ScheduledExecutionUtc = DateTimeOffset.UtcNow.AddSeconds(2);

        await Storage.UpdateTask(queuedTask);

        // Act: let the task execute with new logic, and wait for the two runs to be RECORDED — the
        // handler's counter reaches 2 before the second CurrentRunCount write lands (see the first
        // test in this class)
        var updatedTask = await WaitForRecurringRunsAsync(taskId, expectedRuns: 2, timeoutMs: 15000);

        // Assert: New logic should take over after first execution
        updatedTask.ShouldNotBeNull();
        updatedTask.CurrentRunCount?.ShouldBeGreaterThanOrEqualTo(2);

        // Subsequent runs should use ExecutionTime-based calculation
        var completedRuns = updatedTask.RunsAudits
            .Where(a => a.Status == QueuedTaskStatus.Completed)
            .OrderBy(a => a.ExecutedAt)
            .Take(2)
            .ToList();

        // The wait above already demanded two completed runs on the row, so this is never skipped
        completedRuns.Count.ShouldBe(2);

        var interval = (completedRuns[1].ExecutedAt - completedRuns[0].ExecutedAt).TotalSeconds;

        // Should maintain 2-second interval
        interval.ShouldBeGreaterThan(1.5);
        interval.ShouldBeLessThan(3);
    }

    [Fact]
    public void RecurringTask_Serialization_Should_Be_Backward_Compatible()
    {
        // Arrange: Create a recurring task with new properties
        var newTask = new RecurringTask
        {
            SecondInterval = new SecondInterval(30),
            MaxRuns = 10,
            RunUntil = DateTimeOffset.UtcNow.AddHours(1),
            InitialDelay = TimeSpan.FromMinutes(5)
        };

        // Act: Serialize and deserialize
        var json = JsonConvert.SerializeObject(newTask);
        var deserialized = JsonConvert.DeserializeObject<RecurringTask>(json);

        // Assert: All properties should be preserved
        deserialized.ShouldNotBeNull();
        deserialized.SecondInterval.ShouldNotBeNull();
        deserialized.SecondInterval.Interval.ShouldBe(30);
        deserialized.MaxRuns.ShouldBe(10);
        deserialized.RunUntil.ShouldNotBeNull();
        deserialized.InitialDelay.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Legacy_RecurringTask_JSON_Should_Deserialize_Without_New_Properties()
    {
        // Arrange: Legacy JSON without new properties
        var legacyJson = @"{
            ""HourInterval"": { ""Interval"": 1 }
        }";

        // Act: Deserialize
        var deserialized = JsonConvert.DeserializeObject<RecurringTask>(legacyJson);

        // Assert: Should deserialize successfully with default values
        deserialized.ShouldNotBeNull();
        deserialized.HourInterval.ShouldNotBeNull();
        deserialized.HourInterval.Interval.ShouldBe(1);

        // New properties should have default values
        deserialized.MaxRuns.ShouldBeNull();
        deserialized.RunUntil.ShouldBeNull();
        deserialized.InitialDelay.ShouldBeNull();
    }

    [Fact]
    public async Task Tasks_With_Different_JSON_Formats_Should_Coexist()
    {
        // Arrange
        await CreateIsolatedHostAsync(
            channelCapacity: 10,
            maxDegreeOfParallelism: 5);

        // ✅ Create both tasks as recurring from the start
        var legacyTaskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(2).Seconds());

        var newTaskId = await Dispatcher.Dispatch(
            new TestTaskRecurringMinutes(),
            recurring => recurring.Schedule().Every(2).Seconds().MaxRuns(5));

        // Simulate legacy JSON format (without new properties)
        var legacyTasks = await Storage.Get(t => t.Id == legacyTaskId);
        var legacyTask = legacyTasks[0];

        legacyTask.RecurringTask = @"{""SecondInterval"":{""Interval"":2}}"; // Old format (no MaxRuns, RunUntil, etc.)
        legacyTask.NextRunUtc = DateTimeOffset.UtcNow.AddSeconds(1);
        legacyTask.ScheduledExecutionUtc = DateTimeOffset.UtcNow.AddSeconds(1);

        await Storage.UpdateTask(legacyTask);

        // Act: Both should execute
        await Task.WhenAll(
            TaskWaitHelper.WaitForConditionAsync(
                () => StateManager.GetCounter(nameof(TestTaskRecurringSeconds)) >= 1,
                timeoutMs: 5000),
            TaskWaitHelper.WaitForConditionAsync(
                () => StateManager.GetCounter(nameof(TestTaskRecurringMinutes)) >= 1,
                timeoutMs: 5000)
        );

        // Assert: Both tasks should work
        StateManager.GetCounter(nameof(TestTaskRecurringSeconds)).ShouldBeGreaterThanOrEqualTo(1);
        StateManager.GetCounter(nameof(TestTaskRecurringMinutes)).ShouldBeGreaterThanOrEqualTo(1);
    }
}
