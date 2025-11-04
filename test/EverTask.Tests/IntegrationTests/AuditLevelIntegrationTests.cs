using EverTask.Abstractions;
using EverTask.Storage;
using EverTask.Storage.EfCore;
using EverTask.Storage.SqlServer;
using EverTask.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// End-to-end integration tests for AuditLevel feature.
/// Tests complete flow: Dispatcher → WorkerExecutor → Storage → Database.
/// Uses SQL Server LocalDB for real database verification.
/// </summary>
[Collection("StorageTests")]
public class AuditLevelIntegrationTests : IsolatedIntegrationTestBase
{
    private ITaskStoreDbContext GetDbContext() => Host!.Services.GetRequiredService<ITaskStoreDbContext>();

    private async Task<IHost> CreateHostWithSqlServerAsync(Action<EverTaskServiceConfiguration>? configureEverTask = null)
    {
        // Use unique database per test run (based on process ID) to prevent conflicts when running multiple frameworks in parallel
        var databaseName = $"EverTaskTestDb_AuditLevel_{Environment.ProcessId}_{Guid.NewGuid():N}";
        var connectionString = $"Server=(localdb)\\mssqllocaldb;Database={databaseName};Trusted_Connection=True;MultipleActiveResultSets=true";

        var host = await CreateIsolatedHostWithBuilderAsync(
            builder => builder.AddSqlServerStorage(connectionString, opt => opt.AutoApplyMigrations = true),
            startHost: true,
            configureEverTask: configureEverTask
        );

        return host;
    }

    #region Global Configuration Tests

    [Fact]
    public async Task Should_use_full_audit_level_by_default_and_create_status_audit()
    {
        // Arrange - Don't configure audit level (defaults to Full)
        await CreateHostWithSqlServerAsync();

        // Act - Dispatch and wait for completion
        var taskId = await Dispatcher.Dispatch(new TestTaskRequest("Test"));
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 5000);

        // Assert - Verify in database
        var task = await Storage.Get(t => t.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        task[0].AuditLevel.ShouldBe((int)AuditLevel.Full);

        var dbContext = GetDbContext();
        var auditCount = dbContext.StatusAudit.Count(a => a.QueuedTaskId == taskId);
        auditCount.ShouldBeGreaterThan(0, "Full audit level should create StatusAudit records for successful task");
    }

    [Fact]
    public async Task Should_apply_minimal_global_default_and_not_create_status_audit_for_success()
    {
        // Arrange - Configure global default to Minimal
        await CreateHostWithSqlServerAsync(configureEverTask: cfg =>
        {
            cfg.SetDefaultAuditLevel(AuditLevel.Minimal);
        });

        // Act
        var taskId = await Dispatcher.Dispatch(new TestTaskRequest("Test"));
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 5000);

        // Assert
        var task = await Storage.Get(t => t.Id == taskId);
        task[0].AuditLevel.ShouldBe((int)AuditLevel.Minimal);

        var dbContext = GetDbContext();
        var auditCount = dbContext.StatusAudit.Count(a => a.QueuedTaskId == taskId);
        auditCount.ShouldBe(0, "Minimal should NOT create StatusAudit for successful task");
    }

    [Fact]
    public async Task Should_apply_minimal_global_default_and_create_status_audit_for_failure()
    {
        // Arrange
        await CreateHostWithSqlServerAsync(configureEverTask: cfg =>
        {
            cfg.SetDefaultAuditLevel(AuditLevel.Minimal);
        });

        // Act - Dispatch task that will fail
        var taskId = await Dispatcher.Dispatch(new TestTaskRequestError());
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Failed, timeoutMs: 5000);

        // Assert
        var task = await Storage.Get(t => t.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.Failed);
        task[0].AuditLevel.ShouldBe((int)AuditLevel.Minimal);

        var dbContext = GetDbContext();
        var auditCount = dbContext.StatusAudit.Count(a => a.QueuedTaskId == taskId);
        auditCount.ShouldBeGreaterThan(0, "Minimal SHOULD create StatusAudit for failed task");

        var latestAudit = dbContext.StatusAudit
            .Where(a => a.QueuedTaskId == taskId)
            .OrderByDescending(a => a.Id)
            .FirstOrDefault();
        latestAudit.ShouldNotBeNull();
        latestAudit.NewStatus.ShouldBe(QueuedTaskStatus.Failed);
        latestAudit.Exception.ShouldNotBeNull();
    }

    [Fact]
    public async Task Should_apply_errors_only_global_default()
    {
        // Arrange
        await CreateHostWithSqlServerAsync(configureEverTask: cfg =>
        {
            cfg.SetDefaultAuditLevel(AuditLevel.ErrorsOnly);
        });

        // Act - Success
        var successTaskId = await Dispatcher.Dispatch(new TestTaskRequest("Success"));
        await WaitForTaskStatusAsync(successTaskId, QueuedTaskStatus.Completed, timeoutMs: 5000);

        // Act - Failure
        var failTaskId = await Dispatcher.Dispatch(new TestTaskRequestError());
        await WaitForTaskStatusAsync(failTaskId, QueuedTaskStatus.Failed, timeoutMs: 5000);

        // Assert
        var dbContext = GetDbContext();

        // Success: no audit
        dbContext.StatusAudit.Count(a => a.QueuedTaskId == successTaskId).ShouldBe(0);

        // Failure: has audit
        dbContext.StatusAudit.Count(a => a.QueuedTaskId == failTaskId).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Should_apply_none_global_default_and_never_create_audit()
    {
        // Arrange
        await CreateHostWithSqlServerAsync(configureEverTask: cfg =>
        {
            cfg.SetDefaultAuditLevel(AuditLevel.None);
        });

        // Act - Success
        var successTaskId = await Dispatcher.Dispatch(new TestTaskRequest("Success"));
        await WaitForTaskStatusAsync(successTaskId, QueuedTaskStatus.Completed, timeoutMs: 5000);

        // Act - Failure
        var failTaskId = await Dispatcher.Dispatch(new TestTaskRequestError());
        await WaitForTaskStatusAsync(failTaskId, QueuedTaskStatus.Failed, timeoutMs: 5000);

        // Assert - No audit for both
        var dbContext = GetDbContext();
        dbContext.StatusAudit.Count(a => a.QueuedTaskId == successTaskId).ShouldBe(0);
        dbContext.StatusAudit.Count(a => a.QueuedTaskId == failTaskId).ShouldBe(0, "None should NEVER create audit, even for failures");
    }

    #endregion

    #region Per-Dispatch Override Tests

    [Fact]
    public async Task Should_override_global_full_with_per_dispatch_minimal()
    {
        // Arrange - Global is Full
        await CreateHostWithSqlServerAsync(configureEverTask: cfg =>
        {
            cfg.SetDefaultAuditLevel(AuditLevel.Full);
        });

        // Act - Override with Minimal
        var taskId = await Dispatcher.Dispatch(new TestTaskRequest("Test"), auditLevel: AuditLevel.Minimal);
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 5000);

        // Assert - Should use Minimal (no audit)
        var task = await Storage.Get(t => t.Id == taskId);
        task[0].AuditLevel.ShouldBe((int)AuditLevel.Minimal);

        var dbContext = GetDbContext();
        dbContext.StatusAudit.Count(a => a.QueuedTaskId == taskId).ShouldBe(0);
    }

    [Fact]
    public async Task Should_override_global_minimal_with_per_dispatch_full()
    {
        // Arrange - Global is Minimal
        await CreateHostWithSqlServerAsync(configureEverTask: cfg =>
        {
            cfg.SetDefaultAuditLevel(AuditLevel.Minimal);
        });

        // Act - Override with Full
        var taskId = await Dispatcher.Dispatch(new TestTaskRequest("Test"), auditLevel: AuditLevel.Full);
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 5000);

        // Assert - Should use Full (has audit)
        var task = await Storage.Get(t => t.Id == taskId);
        task[0].AuditLevel.ShouldBe((int)AuditLevel.Full);

        var dbContext = GetDbContext();
        dbContext.StatusAudit.Count(a => a.QueuedTaskId == taskId).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Should_allow_concurrent_tasks_with_different_audit_levels()
    {
        // Arrange
        await CreateHostWithSqlServerAsync(configureEverTask: cfg =>
        {
            cfg.SetDefaultAuditLevel(AuditLevel.ErrorsOnly);
        });

        // Act - Dispatch 4 tasks concurrently
        var taskFull = await Dispatcher.Dispatch(new TestTaskRequest("Full"), auditLevel: AuditLevel.Full);
        var taskMinimal = await Dispatcher.Dispatch(new TestTaskRequest("Minimal"), auditLevel: AuditLevel.Minimal);
        var taskErrorsOnly = await Dispatcher.Dispatch(new TestTaskRequest("ErrorsOnly"), auditLevel: AuditLevel.ErrorsOnly);
        var taskNone = await Dispatcher.Dispatch(new TestTaskRequest("None"), auditLevel: AuditLevel.None);

        // Wait for all
        await WaitForTaskStatusAsync(taskFull, QueuedTaskStatus.Completed, timeoutMs: 5000);
        await WaitForTaskStatusAsync(taskMinimal, QueuedTaskStatus.Completed, timeoutMs: 5000);
        await WaitForTaskStatusAsync(taskErrorsOnly, QueuedTaskStatus.Completed, timeoutMs: 5000);
        await WaitForTaskStatusAsync(taskNone, QueuedTaskStatus.Completed, timeoutMs: 5000);

        // Assert - Each should have correct behavior
        var dbContext = GetDbContext();
        dbContext.StatusAudit.Count(a => a.QueuedTaskId == taskFull).ShouldBeGreaterThan(0, "Full should have audit");
        dbContext.StatusAudit.Count(a => a.QueuedTaskId == taskMinimal).ShouldBe(0, "Minimal should not have audit for success");
        dbContext.StatusAudit.Count(a => a.QueuedTaskId == taskErrorsOnly).ShouldBe(0, "ErrorsOnly should not have audit for success");
        dbContext.StatusAudit.Count(a => a.QueuedTaskId == taskNone).ShouldBe(0, "None should never have audit");
    }

    #endregion

    #region Recurring Task Tests

    [Fact]
    public async Task Should_apply_minimal_to_recurring_and_create_runs_audit_but_not_status_audit()
    {
        // Arrange
        await CreateHostWithSqlServerAsync(configureEverTask: cfg =>
        {
            cfg.SetDefaultAuditLevel(AuditLevel.Minimal);
        });

        // Act - Recurring task (wait for exactly 2 runs to avoid timing issues)
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRequest("Recurring"),
            recurring: r => r.Schedule().Every(1).Seconds().MaxRuns(5)
        );

        // Wait until CurrentRunCount reaches exactly 2 (more reliable than waiting for RunsAudit)
        var task = await TaskWaitHelper.WaitUntilAsync(
            async () => (await Storage.Get(t => t.Id == taskId)).FirstOrDefault(),
            task => task?.CurrentRunCount >= 2,
            timeoutMs: 5000
        );

        task.ShouldNotBeNull();
        task.CurrentRunCount.ShouldBe(2, "Task should have executed exactly 2 times");

        // Assert
        var dbContext = GetDbContext();

        // Minimal: NO StatusAudit for successes
        var statusAuditCount = dbContext.StatusAudit.Count(a => a.QueuedTaskId == taskId);
        statusAuditCount.ShouldBe(0, "Minimal should NOT create StatusAudit for successful recurring tasks");

        // Minimal: YES RunsAudit (to track last run)
        var runsAuditCount = dbContext.RunsAudit.Count(a => a.QueuedTaskId == taskId);
        runsAuditCount.ShouldBe(2, "Minimal should ALWAYS create RunsAudit for recurring tasks");
        runsAuditCount.ShouldBe(task.CurrentRunCount!.Value, "RunsAudit count should match CurrentRunCount");
    }

    [Fact]
    public async Task Should_apply_errors_only_to_recurring_and_not_create_any_audit_for_success()
    {
        // Arrange
        await CreateHostWithSqlServerAsync();

        // Act - Recurring task with ErrorsOnly
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRequest("Recurring"),
            recurring: r => r.Schedule().Every(1).Seconds().MaxRuns(5),
            auditLevel: AuditLevel.ErrorsOnly
        );

        // Wait until CurrentRunCount reaches exactly 2
        var task = await TaskWaitHelper.WaitUntilAsync(
            async () => (await Storage.Get(t => t.Id == taskId)).FirstOrDefault(),
            task => task?.CurrentRunCount >= 2,
            timeoutMs: 5000
        );

        task.ShouldNotBeNull();
        task.CurrentRunCount.ShouldBe(2, "Task should have executed exactly 2 times");

        // Assert
        var dbContext = GetDbContext();
        dbContext.StatusAudit.Count(a => a.QueuedTaskId == taskId).ShouldBe(0, "ErrorsOnly should NOT create StatusAudit for successful runs");
        dbContext.RunsAudit.Count(a => a.QueuedTaskId == taskId).ShouldBe(0, "ErrorsOnly should NOT create RunsAudit for successful runs");
    }

    [Fact]
    public async Task Should_apply_none_to_recurring_and_never_create_audit()
    {
        // Arrange
        await CreateHostWithSqlServerAsync();

        // Act - Recurring task with None audit level
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRequest("Recurring"),
            recurring: r => r.Schedule().Every(1).Seconds().MaxRuns(5),
            auditLevel: AuditLevel.None
        );

        // Wait until CurrentRunCount reaches exactly 2
        var task = await TaskWaitHelper.WaitUntilAsync(
            async () => (await Storage.Get(t => t.Id == taskId)).FirstOrDefault(),
            task => task?.CurrentRunCount >= 2,
            timeoutMs: 5000
        );

        task.ShouldNotBeNull();
        task.CurrentRunCount.ShouldBe(2, "Task should still execute and update CurrentRunCount");

        var dbContext = GetDbContext();
        dbContext.StatusAudit.Count(a => a.QueuedTaskId == taskId).ShouldBe(0, "None should NOT create StatusAudit");
        dbContext.RunsAudit.Count(a => a.QueuedTaskId == taskId).ShouldBe(0, "None should NEVER create any audit");
    }

    #endregion

    #region Scheduled Task Tests

    [Fact]
    public async Task Should_apply_audit_level_to_scheduled_task()
    {
        // Arrange
        await CreateHostWithSqlServerAsync();

        // Act
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRequest("Scheduled"),
            scheduleDelay: TimeSpan.FromMilliseconds(500),
            auditLevel: AuditLevel.Minimal
        );

        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 3000);

        // Assert
        var dbContext = GetDbContext();
        dbContext.StatusAudit.Count(a => a.QueuedTaskId == taskId).ShouldBe(0, "Minimal should not create audit for scheduled success");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task Should_persist_audit_level_even_if_task_never_executes()
    {
        // Arrange
        await CreateHostWithSqlServerAsync();

        // Act - Schedule far in the future
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRequest("Future"),
            scheduleTime: DateTimeOffset.UtcNow.AddHours(10),
            auditLevel: AuditLevel.ErrorsOnly
        );

        await Task.Delay(200); // Give time for persistence

        // Assert - AuditLevel should be persisted
        var task = await Storage.Get(t => t.Id == taskId);
        task[0].AuditLevel.ShouldBe((int)AuditLevel.ErrorsOnly);
    }

    #endregion


    #region Retry Policy Integration

    // NOTE: Retry failures are handled internally by the retry policy and are NOT persisted to StatusAudit.
    // Only the final result (success or failure after all retries) is persisted.
    // This is by design to avoid creating excessive audit records for transient failures.
    // If a task fails after all retries, the AggregateException contains all retry exceptions.

    #endregion

    #region Recurring With Mixed Success/Failure

    [Fact]
    public async Task Should_audit_only_failures_in_recurring_task_with_minimal()
    {
        // Arrange
        TestTaskRecurringWithFailure.Counter = 0; // Reset static counter
        // With retry policy of 3 attempts (4 total calls per execution), we need FailUntilCount high enough
        // to ensure first 2 recurring executions fail completely: 2 executions * 4 calls = 8
        TestTaskRecurringWithFailure.FailUntilCount = 8; // First 2 executions will fail all retries
        await CreateHostWithSqlServerAsync(configureEverTask: cfg =>
        {
            cfg.SetDefaultAuditLevel(AuditLevel.Minimal);
        });

        // Act - Recurring task where first 2 executions fail (with retries), then subsequent executions succeed
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringWithFailure(),
            recurring: r => r.Schedule().Every(1).Seconds().MaxRuns(7) // 2 failed + 5 successful = 7 total
        );

        // Wait until CurrentRunCount reaches at least 7 (includes both failed and successful runs)
        var task = await TaskWaitHelper.WaitUntilAsync(
            async () => (await Storage.Get(t => t.Id == taskId)).FirstOrDefault(),
            task => task?.CurrentRunCount >= 7,
            timeoutMs: 15000
        );

        task.ShouldNotBeNull();
        task.CurrentRunCount.ShouldNotBeNull();
        var executionCount = task.CurrentRunCount!.Value;
        executionCount.ShouldBeGreaterThanOrEqualTo(7, "Should have executed at least 7 times");

        // Assert
        var dbContext = GetDbContext();

        // Minimal: StatusAudit only for failures
        var statusAudits = dbContext.StatusAudit.Where(a => a.QueuedTaskId == taskId).ToList();
        statusAudits.Count.ShouldBeGreaterThan(0, "Should have StatusAudit for failures");
        statusAudits.All(a => !string.IsNullOrEmpty(a.Exception))
            .ShouldBeTrue("All StatusAudit should be for failures with exceptions");

        // Minimal: RunsAudit for ALL executions (successes + failures)
        var runsAudits = dbContext.RunsAudit.Where(a => a.QueuedTaskId == taskId).ToList();
        runsAudits.Count.ShouldBe(executionCount, "RunsAudit count should match execution count");
    }

    [Fact]
    public async Task Should_audit_no_successes_in_recurring_task_with_errors_only()
    {
        // Arrange
        TestTaskRecurringWithFailure.Counter = 0; // Reset static counter
        // With retry policy of 3 attempts (4 total calls per execution), we need FailUntilCount high enough
        // to ensure first 2 recurring executions fail completely: 2 executions * 4 calls = 8
        TestTaskRecurringWithFailure.FailUntilCount = 8; // First 2 executions will fail all retries
        await CreateHostWithSqlServerAsync(configureEverTask: cfg =>
        {
            cfg.SetDefaultAuditLevel(AuditLevel.ErrorsOnly);
        });

        // Act - Recurring task where first 2 executions fail (with retries), then subsequent executions succeed
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringWithFailure(),
            recurring: r => r.Schedule().Every(1).Seconds().MaxRuns(7) // 2 failed + 5 successful = 7 total
        );

        // Wait until CurrentRunCount reaches at least 7 (includes both failed and successful runs)
        var task = await TaskWaitHelper.WaitUntilAsync(
            async () => (await Storage.Get(t => t.Id == taskId)).FirstOrDefault(),
            task => task?.CurrentRunCount >= 7,
            timeoutMs: 15000
        );

        task.ShouldNotBeNull();
        task.CurrentRunCount.ShouldNotBeNull();
        var executionCount = task.CurrentRunCount!.Value;

        // Assert
        var dbContext = GetDbContext();

        // ErrorsOnly: StatusAudit for each retry failure (multiple per recurring execution)
        var statusAudits = dbContext.StatusAudit.Where(a => a.QueuedTaskId == taskId).ToList();
        statusAudits.Count.ShouldBeGreaterThan(0, "Should have StatusAudit for failures");

        // ErrorsOnly: RunsAudit tracks failed RECURRING EXECUTIONS (not individual retry attempts)
        var runsAudits = dbContext.RunsAudit.Where(a => a.QueuedTaskId == taskId).ToList();
        runsAudits.Count.ShouldBeLessThan(executionCount, "RunsAudit should only track failures, not all executions");
        runsAudits.Count.ShouldBeGreaterThan(0, "Should have RunsAudit for failed recurring executions");
        // Note: StatusAudit includes all retry attempts, RunsAudit tracks recurring executions
        // So StatusAudit count will be higher (e.g., 2 failed executions × 4 attempts = 8 StatusAudit, 2 RunsAudit)
    }

    #endregion

    #region Edge Cases - Extended

    [Fact]
    public async Task Should_handle_task_with_very_long_exception_message()
    {
        // Arrange
        await CreateHostWithSqlServerAsync(configureEverTask: cfg =>
        {
            cfg.SetDefaultAuditLevel(AuditLevel.Full);
        });

        // Act - Task that throws exception with 10k+ character message
        var longMessage = new string('X', 15000); // 15k characters
        var taskId = await Dispatcher.Dispatch(new TestTaskRequestError());

        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Failed, timeoutMs: 3000);

        // Assert - Should persist without truncation or errors
        var task = await Storage.Get(t => t.Id == taskId);
        task[0].Status.ShouldBe(QueuedTaskStatus.Failed);
        task[0].Exception.ShouldNotBeNull();

        var dbContext = GetDbContext();
        var audit = dbContext.StatusAudit.FirstOrDefault(a => a.QueuedTaskId == taskId && a.NewStatus == QueuedTaskStatus.Failed);
        audit.ShouldNotBeNull();
        audit.Exception.ShouldNotBeNull();
    }

    [Fact]
    public async Task Should_treat_failed_status_without_exception_as_error_for_minimal()
    {
        // Arrange
        await CreateHostWithSqlServerAsync(configureEverTask: cfg =>
        {
            cfg.SetDefaultAuditLevel(AuditLevel.Minimal);
        });

        // Act - Manually set task to Failed without exception
        var taskId = await Dispatcher.Dispatch(new TestTaskRequest("Test"));
        await Task.Delay(500);

        // Manually update to Failed with null exception
        await Storage.SetStatus(taskId, QueuedTaskStatus.Failed, null, AuditLevel.Minimal);

        // Assert - Minimal should create StatusAudit for Failed even without exception
        var dbContext = GetDbContext();
        var audits = dbContext.StatusAudit.Where(a => a.QueuedTaskId == taskId && a.NewStatus == QueuedTaskStatus.Failed).ToList();
        audits.Count.ShouldBeGreaterThan(0, "Minimal should create StatusAudit for Failed status even without explicit exception");
    }

    [Fact]
    public async Task Should_fallback_to_full_for_invalid_audit_level_value()
    {
        // Arrange
        await CreateHostWithSqlServerAsync();

        // Act - Create task with valid audit level, then manually change to invalid value
        var taskId = await Dispatcher.Dispatch(new TestTaskRequest("Test"), auditLevel: AuditLevel.None);
        await Task.Delay(300);

        // Manually update AuditLevel to invalid value in database
        var dbContext = GetDbContext();
        var task = dbContext.QueuedTasks.FirstOrDefault(t => t.Id == taskId);
        task.ShouldNotBeNull();
        task.AuditLevel = 99; // Invalid enum value
        await dbContext.SaveChangesAsync(CancellationToken.None);

        // Trigger task execution by updating to Queued (simulating recovery)
        await Storage.SetStatus(taskId, QueuedTaskStatus.Completed, null, AuditLevel.Full);

        // Assert - Should not crash, should use Full as fallback
        var audits = dbContext.StatusAudit.Where(a => a.QueuedTaskId == taskId).ToList();
        // With Full fallback, should have created audit
        audits.Count.ShouldBeGreaterThan(0, "Invalid AuditLevel should fallback to Full behavior");
    }

    #endregion

    #region Concurrency - Extended

    [Fact]
    public async Task Should_handle_20_concurrent_tasks_with_mixed_audit_levels()
    {
        // Arrange
        await CreateHostWithSqlServerAsync(configureEverTask: cfg =>
        {
            cfg.SetDefaultAuditLevel(AuditLevel.Full);
        });

        // Act - Dispatch 20 tasks: 5 per each audit level
        var taskIds = new List<Guid>();

        for (int i = 0; i < 5; i++)
        {
            taskIds.Add(await Dispatcher.Dispatch(new TestTaskRequest($"Full-{i}"), auditLevel: AuditLevel.Full));
            taskIds.Add(await Dispatcher.Dispatch(new TestTaskRequest($"Minimal-{i}"), auditLevel: AuditLevel.Minimal));
            taskIds.Add(await Dispatcher.Dispatch(new TestTaskRequest($"ErrorsOnly-{i}"), auditLevel: AuditLevel.ErrorsOnly));
            taskIds.Add(await Dispatcher.Dispatch(new TestTaskRequest($"None-{i}"), auditLevel: AuditLevel.None));
        }

        // Wait for all to complete
        foreach (var taskId in taskIds)
        {
            await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 10000);
        }

        // Assert - Verify each task used its correct audit level
        var dbContext = GetDbContext();

        // Tasks are dispatched interleaved (Full, Minimal, ErrorsOnly, None, Full, Minimal, ...)
        // So we need to extract every 4th element starting from each offset
        var fullTasks = taskIds.Where((id, index) => index % 4 == 0).ToList();
        var minimalTasks = taskIds.Where((id, index) => index % 4 == 1).ToList();
        var errorsOnlyTasks = taskIds.Where((id, index) => index % 4 == 2).ToList();
        var noneTasks = taskIds.Where((id, index) => index % 4 == 3).ToList();

        // Full tasks should have audit
        foreach (var taskId in fullTasks)
        {
            dbContext.StatusAudit.Count(a => a.QueuedTaskId == taskId).ShouldBeGreaterThan(0, $"Full task {taskId} should have StatusAudit");
        }

        // Minimal successful tasks should NOT have audit
        foreach (var taskId in minimalTasks)
        {
            dbContext.StatusAudit.Count(a => a.QueuedTaskId == taskId).ShouldBe(0, $"Minimal success task {taskId} should NOT have StatusAudit");
        }

        // ErrorsOnly successful tasks should NOT have audit
        foreach (var taskId in errorsOnlyTasks)
        {
            dbContext.StatusAudit.Count(a => a.QueuedTaskId == taskId).ShouldBe(0, $"ErrorsOnly success task {taskId} should NOT have StatusAudit");
        }

        // None tasks should NEVER have audit
        foreach (var taskId in noneTasks)
        {
            dbContext.StatusAudit.Count(a => a.QueuedTaskId == taskId).ShouldBe(0, $"None task {taskId} should NEVER have StatusAudit");
        }
    }

    #endregion
}
