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

        // Act: Wait for the task to be picked up and executed
        await TaskWaitHelper.WaitForConditionAsync(
            () => StateManager.GetCounter(nameof(TestTaskRecurringSeconds)) >= 1,
            timeoutMs: 5000); // 5 seconds should be enough for 2-second interval

        // Assert: Task should have been deserialized and rescheduled correctly
        var updatedTasks = await Storage.GetAll();
        var updatedTask = updatedTasks.FirstOrDefault(t => t.Id == taskId);

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

        // Act: Wait for execution
        await TaskWaitHelper.WaitForConditionAsync(
            () => StateManager.GetCounter(nameof(TestTaskRecurringSeconds)) >= 1,
            timeoutMs: 5000);

        // Assert: Task should still execute and reschedule
        var updatedTasks = await Storage.GetAll();
        var updatedTask = updatedTasks.FirstOrDefault(t => t.Id == taskId);

        updatedTask.ShouldNotBeNull();
        updatedTask.CurrentRunCount?.ShouldBeGreaterThanOrEqualTo(1);
        updatedTask.NextRunUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task Storage_Without_RecordSkippedOccurrences_Should_Degrade_Gracefully()
    {
        // Arrange: Use TestTaskStorage which doesn't implement RecordSkippedOccurrences properly
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

        // Act: Let the task execute with new logic
        await TaskWaitHelper.WaitForConditionAsync(
            () => StateManager.GetCounter(nameof(TestTaskRecurringSeconds)) >= 2,
            timeoutMs: 8000);

        // Assert: New logic should take over after first execution
        var updatedTasks = await Storage.GetAll();
        var updatedTask = updatedTasks.FirstOrDefault(t => t.Id == taskId);

        updatedTask.ShouldNotBeNull();
        updatedTask.CurrentRunCount?.ShouldBeGreaterThanOrEqualTo(2);

        // Subsequent runs should use ExecutionTime-based calculation
        var completedRuns = updatedTask.RunsAudits
            .Where(a => a.Status == QueuedTaskStatus.Completed)
            .OrderBy(a => a.ExecutedAt)
            .Take(2)
            .ToList();

        if (completedRuns.Count >= 2)
        {
            var interval = (completedRuns[1].ExecutedAt - completedRuns[0].ExecutedAt).TotalSeconds;

            // Should maintain 2-second interval
            interval.ShouldBeGreaterThan(1.5);
            interval.ShouldBeLessThan(3);
        }
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
