using EverTask.Monitor.Api.DTOs.Tasks;
using EverTask.Tests.Monitoring.TestHelpers;

namespace EverTask.Tests.Monitoring.API.Controllers;

public class ExecutionLogsEndpointTests : MonitoringTestBase
{
    private TestDataSeeder Seeder => new(Storage);

    [Fact]
    public async Task Should_return_logs_when_task_exists()
    {
        // Arrange: Create task with logs
        var taskId = await Seeder.CreateTaskWithLogsAsync(logCount: 10, level: "Information");

        // Act
        var response = await Client.GetAsync($"/monitoring/api/tasks/{taskId}/execution-logs");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await DeserializeResponseAsync<ExecutionLogsResponse>(response);
        result.ShouldNotBeNull();
        result.Logs.Count.ShouldBe(10);
        result.TotalCount.ShouldBe(10);
        result.Skip.ShouldBe(0);
        result.Take.ShouldBe(100); // Default take value
    }

    [Fact]
    public async Task Should_return_empty_when_no_logs()
    {
        // Arrange: Create task without logs
        var taskId = Guid.NewGuid();
        var task = new QueuedTask
        {
            Id = taskId,
            Type = "TestTask",
            Handler = "TestHandler",
            Request = "{}",
            Status = QueuedTaskStatus.Completed,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await Storage.Persist(task);

        // Act
        var response = await Client.GetAsync($"/monitoring/api/tasks/{taskId}/execution-logs");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await DeserializeResponseAsync<ExecutionLogsResponse>(response);
        result.ShouldNotBeNull();
        result.Logs.ShouldBeEmpty();
        result.TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task Should_return_200_with_empty_when_task_not_found()
    {
        // Arrange: Non-existent task ID
        var nonExistentId = Guid.NewGuid();

        // Act
        var response = await Client.GetAsync($"/monitoring/api/tasks/{nonExistentId}/execution-logs");

        // Assert
        // Note: Current implementation returns 200 with empty logs rather than 404
        // This is acceptable for logs endpoint as it simplifies frontend logic
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await DeserializeResponseAsync<ExecutionLogsResponse>(response);
        result.ShouldNotBeNull();
        result.Logs.ShouldBeEmpty();
        result.TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task Should_paginate_when_skip_take_provided()
    {
        // Arrange: Task with 250 logs
        var taskId = await Seeder.CreateTaskWithLogsAsync(logCount: 250, level: "Debug");

        // Act: Skip first 100, take next 100
        var response = await Client.GetAsync($"/monitoring/api/tasks/{taskId}/execution-logs?skip=100&take=100");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await DeserializeResponseAsync<ExecutionLogsResponse>(response);
        result.ShouldNotBeNull();
        result.Logs.Count.ShouldBe(100);
        result.TotalCount.ShouldBe(250);
        result.Skip.ShouldBe(100);
        result.Take.ShouldBe(100);

        // Verify we got logs 101-200 (sequence numbers 100-199)
        result.Logs.First().SequenceNumber.ShouldBe(100);
        result.Logs.Last().SequenceNumber.ShouldBe(199);
    }

    [Fact]
    public async Task Should_filter_by_level_when_level_provided()
    {
        // Arrange: Task with mixed log levels
        var taskId = Guid.NewGuid();
        var task = new QueuedTask
        {
            Id = taskId,
            Type = "TestTask",
            Handler = "TestHandler",
            Request = "{}",
            Status = QueuedTaskStatus.Completed,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await Storage.Persist(task);

        var logs = new List<TaskExecutionLog>
        {
            new() { Id = Guid.NewGuid(), TaskId = taskId, TimestampUtc = DateTimeOffset.UtcNow, Level = "Information", Message = "Info 1", SequenceNumber = 0 },
            new() { Id = Guid.NewGuid(), TaskId = taskId, TimestampUtc = DateTimeOffset.UtcNow, Level = "Warning", Message = "Warn 1", SequenceNumber = 1 },
            new() { Id = Guid.NewGuid(), TaskId = taskId, TimestampUtc = DateTimeOffset.UtcNow, Level = "Error", Message = "Error 1", SequenceNumber = 2 },
            new() { Id = Guid.NewGuid(), TaskId = taskId, TimestampUtc = DateTimeOffset.UtcNow, Level = "Information", Message = "Info 2", SequenceNumber = 3 },
            new() { Id = Guid.NewGuid(), TaskId = taskId, TimestampUtc = DateTimeOffset.UtcNow, Level = "Error", Message = "Error 2", SequenceNumber = 4 }
        };
        await Storage.SaveExecutionLogsAsync(taskId, logs, CancellationToken.None);

        // Act: Filter by Error level
        var response = await Client.GetAsync($"/monitoring/api/tasks/{taskId}/execution-logs?level=Error");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await DeserializeResponseAsync<ExecutionLogsResponse>(response);
        result.ShouldNotBeNull();
        result.Logs.Count.ShouldBe(2);
        result.Logs.All(l => l.Level == "Error").ShouldBeTrue();
        result.Logs.Select(l => l.Message).ShouldBe(new[] { "Error 1", "Error 2" });
    }

    [Theory]
    [InlineData("Trace")]
    [InlineData("Debug")]
    [InlineData("Information")]
    [InlineData("Warning")]
    [InlineData("Error")]
    [InlineData("Critical")]
    public async Task Should_filter_correctly_for_each_level(string level)
    {
        // Arrange: Task with all log levels
        var taskId = Guid.NewGuid();
        var task = new QueuedTask
        {
            Id = taskId,
            Type = "TestTask",
            Handler = "TestHandler",
            Request = "{}",
            Status = QueuedTaskStatus.Completed,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await Storage.Persist(task);

        var logs = new List<TaskExecutionLog>
        {
            new() { Id = Guid.NewGuid(), TaskId = taskId, TimestampUtc = DateTimeOffset.UtcNow, Level = "Trace", Message = "Trace msg", SequenceNumber = 0 },
            new() { Id = Guid.NewGuid(), TaskId = taskId, TimestampUtc = DateTimeOffset.UtcNow, Level = "Debug", Message = "Debug msg", SequenceNumber = 1 },
            new() { Id = Guid.NewGuid(), TaskId = taskId, TimestampUtc = DateTimeOffset.UtcNow, Level = "Information", Message = "Info msg", SequenceNumber = 2 },
            new() { Id = Guid.NewGuid(), TaskId = taskId, TimestampUtc = DateTimeOffset.UtcNow, Level = "Warning", Message = "Warning msg", SequenceNumber = 3 },
            new() { Id = Guid.NewGuid(), TaskId = taskId, TimestampUtc = DateTimeOffset.UtcNow, Level = "Error", Message = "Error msg", SequenceNumber = 4 },
            new() { Id = Guid.NewGuid(), TaskId = taskId, TimestampUtc = DateTimeOffset.UtcNow, Level = "Critical", Message = "Critical msg", SequenceNumber = 5 }
        };
        await Storage.SaveExecutionLogsAsync(taskId, logs, CancellationToken.None);

        // Act: Filter by specified level
        var response = await Client.GetAsync($"/monitoring/api/tasks/{taskId}/execution-logs?level={level}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await DeserializeResponseAsync<ExecutionLogsResponse>(response);
        result.ShouldNotBeNull();
        result.Logs.Count.ShouldBe(1);
        result.Logs.First().Level.ShouldBe(level);
    }

    [Fact]
    public async Task Should_order_by_sequence_number_ascending()
    {
        // Arrange: Task with logs in random order
        var taskId = Guid.NewGuid();
        var task = new QueuedTask
        {
            Id = taskId,
            Type = "TestTask",
            Handler = "TestHandler",
            Request = "{}",
            Status = QueuedTaskStatus.Completed,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await Storage.Persist(task);

        var logs = new List<TaskExecutionLog>
        {
            new() { Id = Guid.NewGuid(), TaskId = taskId, TimestampUtc = DateTimeOffset.UtcNow, Level = "Information", Message = "Log 5", SequenceNumber = 5 },
            new() { Id = Guid.NewGuid(), TaskId = taskId, TimestampUtc = DateTimeOffset.UtcNow, Level = "Information", Message = "Log 0", SequenceNumber = 0 },
            new() { Id = Guid.NewGuid(), TaskId = taskId, TimestampUtc = DateTimeOffset.UtcNow, Level = "Information", Message = "Log 3", SequenceNumber = 3 },
            new() { Id = Guid.NewGuid(), TaskId = taskId, TimestampUtc = DateTimeOffset.UtcNow, Level = "Information", Message = "Log 1", SequenceNumber = 1 },
            new() { Id = Guid.NewGuid(), TaskId = taskId, TimestampUtc = DateTimeOffset.UtcNow, Level = "Information", Message = "Log 2", SequenceNumber = 2 },
            new() { Id = Guid.NewGuid(), TaskId = taskId, TimestampUtc = DateTimeOffset.UtcNow, Level = "Information", Message = "Log 4", SequenceNumber = 4 }
        };
        await Storage.SaveExecutionLogsAsync(taskId, logs, CancellationToken.None);

        // Act
        var response = await Client.GetAsync($"/monitoring/api/tasks/{taskId}/execution-logs");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await DeserializeResponseAsync<ExecutionLogsResponse>(response);
        result.ShouldNotBeNull();
        result.Logs.Count.ShouldBe(6);

        // Verify correct order: 0 → 1 → 2 → 3 → 4 → 5
        for (int i = 0; i < 6; i++)
        {
            result.Logs[i].SequenceNumber.ShouldBe(i);
            result.Logs[i].Message.ShouldBe($"Log {i}");
        }
    }

    [Fact]
    public async Task Should_include_exception_details_when_present()
    {
        // Arrange: Task with error log + exception
        var taskId = await Seeder.CreateTaskWithLogsAsync(
            logCount: 5,
            level: "Error",
            includeExceptions: true
        );

        // Act
        var response = await Client.GetAsync($"/monitoring/api/tasks/{taskId}/execution-logs");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await DeserializeResponseAsync<ExecutionLogsResponse>(response);
        result.ShouldNotBeNull();
        result.Logs.Count.ShouldBe(5);

        // Last log should have exception details
        var lastLog = result.Logs.Last();
        lastLog.ExceptionDetails.ShouldNotBeNullOrEmpty();
        lastLog.ExceptionDetails.ShouldContain("InvalidOperationException");
        lastLog.ExceptionDetails.ShouldContain("Test exception");
    }

    [Fact]
    public async Task Should_return_correct_dto_structure()
    {
        // Arrange
        var taskId = await Seeder.CreateTaskWithLogsAsync(logCount: 5);

        // Act
        var response = await Client.GetAsync($"/monitoring/api/tasks/{taskId}/execution-logs");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await DeserializeResponseAsync<ExecutionLogsResponse>(response);
        result.ShouldNotBeNull();

        // Verify ExecutionLogsResponse structure
        result.Logs.ShouldNotBeNull();
        result.TotalCount.ShouldBe(5);
        result.Skip.ShouldBe(0);
        result.Take.ShouldBe(100);

        // Verify ExecutionLogDto structure
        var firstLog = result.Logs.First();
        firstLog.Id.ShouldNotBe(Guid.Empty);
        firstLog.TimestampUtc.ShouldNotBe(default(DateTimeOffset));
        firstLog.Level.ShouldNotBeNullOrEmpty();
        firstLog.Message.ShouldNotBeNullOrEmpty();
        firstLog.SequenceNumber.ShouldBeGreaterThanOrEqualTo(0);
        // ExceptionDetails can be null
    }

    [Fact]
    public async Task Should_handle_pagination_edge_cases()
    {
        // Arrange: Task with exactly 100 logs
        var taskId = await Seeder.CreateTaskWithLogsAsync(logCount: 100);

        // Act 1: Get all logs (default take = 100)
        var response1 = await Client.GetAsync($"/monitoring/api/tasks/{taskId}/execution-logs");
        var result1 = await DeserializeResponseAsync<ExecutionLogsResponse>(response1);

        // Assert 1: Should return all 100 logs
        result1!.Logs.Count.ShouldBe(100);
        result1.TotalCount.ShouldBe(100);

        // Act 2: Try to get beyond available logs
        var response2 = await Client.GetAsync($"/monitoring/api/tasks/{taskId}/execution-logs?skip=100&take=50");
        var result2 = await DeserializeResponseAsync<ExecutionLogsResponse>(response2);

        // Assert 2: Should return empty array (no more logs)
        result2!.Logs.ShouldBeEmpty();
        result2.TotalCount.ShouldBe(100);
        result2.Skip.ShouldBe(100);
    }

    [Fact]
    public async Task Should_handle_case_insensitive_level_filter()
    {
        // Arrange: Task with Error logs
        var taskId = await Seeder.CreateTaskWithLogsAsync(logCount: 3, level: "Error");

        // Act: Use lowercase "error" (case-insensitive)
        var response = await Client.GetAsync($"/monitoring/api/tasks/{taskId}/execution-logs?level=error");

        // Assert: Should still find logs with "Error" level
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await DeserializeResponseAsync<ExecutionLogsResponse>(response);
        result.ShouldNotBeNull();
        result.Logs.Count.ShouldBe(3);
        result.Logs.All(l => l.Level.Equals("Error", StringComparison.OrdinalIgnoreCase)).ShouldBeTrue();
    }
}
