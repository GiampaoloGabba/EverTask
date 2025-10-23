using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using EverTask.Logging;
using EverTask.Storage;

namespace EverTask.Tests.Logging;

public class TaskLogCaptureTests
{
    private readonly Mock<ILogger> _mockLogger;
    private readonly Mock<IGuidGenerator> _mockGuidGenerator;

    public TaskLogCaptureTests()
    {
        _mockLogger = new Mock<ILogger>();
        _mockGuidGenerator = new Mock<IGuidGenerator>();
        _mockGuidGenerator.Setup(x => x.NewDatabaseFriendly()).Returns(Guid.NewGuid());
    }

    [Fact]
    public void TaskLogCapture_WithPersistence_ShouldPersistLogsInOrder()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var capture = new TaskLogCapture(
            _mockLogger.Object,
            taskId,
            _mockGuidGenerator.Object,
            persistLogs: true,
            minPersistLevel: LogLevel.Trace,
            maxPersistedLogs: 100);

        // Act
        capture.LogTrace("Message 1");
        capture.LogDebug("Message 2");
        capture.LogInformation("Message 3");

        // Assert
        var logs = capture.GetPersistedLogs();
        logs.Count.ShouldBe(3);
        logs[0].SequenceNumber.ShouldBe(0);
        logs[0].Message.ShouldBe("Message 1");
        logs[0].Level.ShouldBe("Trace");
        logs[1].SequenceNumber.ShouldBe(1);
        logs[1].Message.ShouldBe("Message 2");
        logs[1].Level.ShouldBe("Debug");
        logs[2].SequenceNumber.ShouldBe(2);
        logs[2].Message.ShouldBe("Message 3");
        logs[2].Level.ShouldBe("Information");

        // Verify ILogger was also called
        _mockLogger.Verify(
            x => x.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(3));
    }

    [Fact]
    public void TaskLogCapture_WithPersistence_ShouldFilterByLogLevel()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var capture = new TaskLogCapture(
            _mockLogger.Object,
            taskId,
            _mockGuidGenerator.Object,
            persistLogs: true,
            minPersistLevel: LogLevel.Information,
            maxPersistedLogs: 100);

        // Act
        capture.LogTrace("Should not be persisted");
        capture.LogDebug("Should not be persisted");
        capture.LogInformation("Should be persisted");
        capture.LogWarning("Should be persisted");

        // Assert - only Information and Warning are persisted
        var logs = capture.GetPersistedLogs();
        logs.Count.ShouldBe(2);
        logs[0].Message.ShouldBe("Should be persisted");
        logs[0].Level.ShouldBe("Information");
        logs[1].Message.ShouldBe("Should be persisted");
        logs[1].Level.ShouldBe("Warning");

        // Verify ILogger was called for ALL logs (not filtered)
        _mockLogger.Verify(
            x => x.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(4));
    }

    [Fact]
    public void TaskLogCapture_WithPersistence_ShouldRespectMaxLogs()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var capture = new TaskLogCapture(
            _mockLogger.Object,
            taskId,
            _mockGuidGenerator.Object,
            persistLogs: true,
            minPersistLevel: LogLevel.Trace,
            maxPersistedLogs: 3);

        // Act
        capture.LogInformation("Log 1");
        capture.LogInformation("Log 2");
        capture.LogInformation("Log 3");
        capture.LogInformation("Log 4 - should not be persisted");
        capture.LogInformation("Log 5 - should not be persisted");

        // Assert
        var logs = capture.GetPersistedLogs();
        logs.Count.ShouldBe(3);
        logs[0].Message.ShouldBe("Log 1");
        logs[1].Message.ShouldBe("Log 2");
        logs[2].Message.ShouldBe("Log 3");
        logs.ShouldNotContain(l => l.Message.Contains("Log 4"));
        logs.ShouldNotContain(l => l.Message.Contains("Log 5"));

        // Verify ILogger was called for ALL logs (not filtered by max)
        _mockLogger.Verify(
            x => x.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(5));
    }

    [Fact]
    public void TaskLogCapture_WithoutPersistence_ShouldNotPersistLogs()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var capture = new TaskLogCapture(
            _mockLogger.Object,
            taskId,
            _mockGuidGenerator.Object,
            persistLogs: false,  // Persistence disabled
            minPersistLevel: LogLevel.Trace,
            maxPersistedLogs: 100);

        // Act
        capture.LogTrace("Message 1");
        capture.LogDebug("Message 2");
        capture.LogInformation("Message 3");

        // Assert - no logs persisted
        var logs = capture.GetPersistedLogs();
        logs.ShouldBeEmpty();

        // Verify ILogger was still called
        _mockLogger.Verify(
            x => x.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(3));
    }

    [Fact]
    public void TaskLogCapture_ShouldCaptureExceptions()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var capture = new TaskLogCapture(
            _mockLogger.Object,
            taskId,
            _mockGuidGenerator.Object,
            persistLogs: true,
            minPersistLevel: LogLevel.Trace,
            maxPersistedLogs: 100);
        var exception = new InvalidOperationException("Test exception");

        // Act
        capture.LogError("Error occurred", exception);

        // Assert
        var logs = capture.GetPersistedLogs();
        logs.Count.ShouldBe(1);
        logs[0].Message.ShouldBe("Error occurred");
        logs[0].Level.ShouldBe("Error");
        logs[0].ExceptionDetails.ShouldNotBeNull();
        logs[0].ExceptionDetails!.ShouldContain("Test exception");
        logs[0].ExceptionDetails!.ShouldContain("InvalidOperationException");
    }

    [Fact]
    public void TaskLogCapture_ShouldBeThreadSafe()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var capture = new TaskLogCapture(
            _mockLogger.Object,
            taskId,
            _mockGuidGenerator.Object,
            persistLogs: true,
            minPersistLevel: LogLevel.Trace,
            maxPersistedLogs: 1000);

        // Act - simulate concurrent logging from multiple threads
        Parallel.For(0, 100, i =>
        {
            capture.LogInformation($"Message {i}");
        });

        // Assert
        var logs = capture.GetPersistedLogs();
        logs.Count.ShouldBe(100);

        // All sequence numbers should be unique (no duplicate IDs)
        var uniqueSequenceNumbers = logs.Select(l => l.SequenceNumber).Distinct().Count();
        uniqueSequenceNumbers.ShouldBe(100);

        // All message numbers should be present (0-99)
        for (int i = 0; i < 100; i++)
        {
            logs.ShouldContain(l => l.Message.Contains($"Message {i}"));
        }
    }

    [Fact]
    public void GetPersistedLogs_ShouldReturnDefensiveCopy()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var capture = new TaskLogCapture(
            _mockLogger.Object,
            taskId,
            _mockGuidGenerator.Object,
            persistLogs: true,
            minPersistLevel: LogLevel.Trace,
            maxPersistedLogs: 100);
        capture.LogInformation("Test");

        // Act
        var logs1 = capture.GetPersistedLogs();
        var logs2 = capture.GetPersistedLogs();

        // Assert - different instances
        logs1.ShouldNotBeSameAs(logs2);

        // Same content
        logs1.Count.ShouldBe(logs2.Count);
        logs1[0].Message.ShouldBe(logs2[0].Message);
    }

    [Fact]
    public void TaskLogCapture_ShouldSetTaskIdOnAllLogs()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var capture = new TaskLogCapture(
            _mockLogger.Object,
            taskId,
            _mockGuidGenerator.Object,
            persistLogs: true,
            minPersistLevel: LogLevel.Trace,
            maxPersistedLogs: 100);

        // Act
        capture.LogTrace("Trace");
        capture.LogDebug("Debug");
        capture.LogInformation("Info");
        capture.LogWarning("Warning");
        capture.LogError("Error");
        capture.LogCritical("Critical");

        // Assert
        var logs = capture.GetPersistedLogs();
        logs.Count.ShouldBe(6);
        logs.ShouldAllBe(l => l.TaskId == taskId);
    }

    [Fact]
    public void TaskLogCapture_ShouldSetTimestampsToUtc()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var capture = new TaskLogCapture(
            _mockLogger.Object,
            taskId,
            _mockGuidGenerator.Object,
            persistLogs: true,
            minPersistLevel: LogLevel.Trace,
            maxPersistedLogs: 100);
        var beforeUtc = DateTimeOffset.UtcNow;

        // Act
        capture.LogInformation("Test message");

        // Assert
        var logs = capture.GetPersistedLogs();
        logs.Count.ShouldBe(1);
        logs[0].TimestampUtc.ShouldBeGreaterThanOrEqualTo(beforeUtc);
        logs[0].TimestampUtc.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow.AddSeconds(1));

        // Verify it's UTC (offset should be zero)
        logs[0].TimestampUtc.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void TaskLogCapture_WithNullMaxLogs_ShouldPersistUnlimited()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var capture = new TaskLogCapture(
            _mockLogger.Object,
            taskId,
            _mockGuidGenerator.Object,
            persistLogs: true,
            minPersistLevel: LogLevel.Trace,
            maxPersistedLogs: null);

        // Act - log more than default capacity
        for (int i = 0; i < 200; i++)
        {
            capture.LogInformation($"Log {i}");
        }

        // Assert - all logs should be persisted
        var logs = capture.GetPersistedLogs();
        logs.Count.ShouldBe(200);
    }

    [Fact]
    public void TaskLogCapture_ShouldHandleWarningWithException()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var capture = new TaskLogCapture(
            _mockLogger.Object,
            taskId,
            _mockGuidGenerator.Object,
            persistLogs: true,
            minPersistLevel: LogLevel.Trace,
            maxPersistedLogs: 100);
        var exception = new ArgumentException("Invalid argument");

        // Act
        capture.LogWarning("Warning with exception", exception);

        // Assert
        var logs = capture.GetPersistedLogs();
        logs.Count.ShouldBe(1);
        logs[0].Level.ShouldBe("Warning");
        logs[0].Message.ShouldBe("Warning with exception");
        logs[0].ExceptionDetails.ShouldNotBeNull();
        logs[0].ExceptionDetails!.ShouldContain("ArgumentException");
        logs[0].ExceptionDetails!.ShouldContain("Invalid argument");
    }

    [Fact]
    public void TaskLogCapture_ShouldHandleCriticalWithException()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var capture = new TaskLogCapture(
            _mockLogger.Object,
            taskId,
            _mockGuidGenerator.Object,
            persistLogs: true,
            minPersistLevel: LogLevel.Trace,
            maxPersistedLogs: 100);
        var exception = new OutOfMemoryException("Out of memory");

        // Act
        capture.LogCritical("Critical failure", exception);

        // Assert
        var logs = capture.GetPersistedLogs();
        logs.Count.ShouldBe(1);
        logs[0].Level.ShouldBe("Critical");
        logs[0].Message.ShouldBe("Critical failure");
        logs[0].ExceptionDetails.ShouldNotBeNull();
        logs[0].ExceptionDetails!.ShouldContain("OutOfMemoryException");
        logs[0].ExceptionDetails!.ShouldContain("Out of memory");
    }

    [Fact]
    public void TaskLogCapture_ShouldAlwaysForwardToILogger()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var capture = new TaskLogCapture(
            _mockLogger.Object,
            taskId,
            _mockGuidGenerator.Object,
            persistLogs: false,  // Persistence disabled
            minPersistLevel: LogLevel.Critical,  // High filter level (shouldn't matter for ILogger)
            maxPersistedLogs: 0);

        // Act - log at various levels
        capture.LogTrace("Trace");
        capture.LogDebug("Debug");
        capture.LogInformation("Info");
        capture.LogWarning("Warning");
        capture.LogError("Error");
        capture.LogCritical("Critical");

        // Assert - all logs forwarded to ILogger despite persistence being disabled
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Trace,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Critical,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        // But no logs should be persisted
        var logs = capture.GetPersistedLogs();
        logs.ShouldBeEmpty();
    }
}
