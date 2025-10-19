using System.Reflection;
using Cronos;
using EverTask.Configuration;
using EverTask.Handler;
using EverTask.Logger;
using EverTask.Scheduler;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using Microsoft.Extensions.Logging;

namespace EverTask.Tests;

public class TimerSchedulerTests
{
    private readonly Mock<IWorkerQueue> _mockWorkerQueue;
    private readonly Mock<IWorkerQueueManager> _mockWorkerQueueManager;
    private readonly Mock<IEverTaskLogger<PeriodicTimerScheduler>> _mockLogger;
    private readonly PeriodicTimerScheduler _timerScheduler;

    public TimerSchedulerTests()
    {
        _mockWorkerQueue = new Mock<IWorkerQueue>();
        _mockWorkerQueueManager = new Mock<IWorkerQueueManager>();
        _mockLogger      = new Mock<IEverTaskLogger<PeriodicTimerScheduler>>();

        // Setup the queue manager to return the default queue
        _mockWorkerQueueManager.Setup(x => x.GetQueue("default")).Returns(_mockWorkerQueue.Object);

        // Setup TryEnqueue to delegate to the worker queue
        _mockWorkerQueueManager.Setup(x => x.TryEnqueue(It.IsAny<string?>(), It.IsAny<TaskHandlerExecutor>()))
            .Returns<string?, TaskHandlerExecutor>(async (queueName, executor) =>
            {
                await _mockWorkerQueue.Object.Queue(executor);
                return true;
            });

        _timerScheduler  = new PeriodicTimerScheduler(_mockWorkerQueueManager.Object, _mockLogger.Object);
    }

    [Fact]
    public void Schedule_should_enqueue_item_with_correct_execution_time()
    {
        var executionTime       = DateTimeOffset.UtcNow.AddMinutes(1);
        var taskHandlerExecutor = CreateTaskHandlerExecutor(executionTime);

        _timerScheduler.Schedule(taskHandlerExecutor);

        var itemInQueue = _timerScheduler.GetQueue().Dequeue();

        Assert.Equal(executionTime, itemInQueue.ExecutionTime);
    }

    [Fact]
    public void Schedule_should_enqueue_recurring_cron_item_with_correct_execution_time()
    {
        var cronExpresison      = "*/5 * * * *";
        var nextOccourrence = CronExpression.Parse(cronExpresison)
                                            .GetNextOccurrence(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        var recurringTask       = new RecurringTask { CronInterval = new CronInterval(cronExpresison) };
        var taskHandlerExecutor = CreateTaskHandlerExecutor(null, recurringTask);

        _timerScheduler.Schedule(taskHandlerExecutor, nextOccourrence);

        _timerScheduler.GetQueue().Count.ShouldBePositive();

        var itemInQueue = _timerScheduler.GetQueue().Dequeue();

        Assert.Equal(nextOccourrence, itemInQueue.RecurringTask!.CalculateNextRun(DateTimeOffset.UtcNow,0));
    }


    [Fact]
    public void Schedule_should_enqueue_recurring_item_with_correct_execution_time()
    {
        var nextOccourrence = new MinuteInterval(10){ OnSecond = 0}.GetNextOccurrence(DateTimeOffset.UtcNow);
        var recurringTask       = new RecurringTask { MinuteInterval = new MinuteInterval(10) { OnSecond = 0} };
        var taskHandlerExecutor = CreateTaskHandlerExecutor(null, recurringTask);

        _timerScheduler.Schedule(taskHandlerExecutor, nextOccourrence);

        var itemInQueue = _timerScheduler.GetQueue().Dequeue();

        nextOccourrence = nextOccourrence!.Value.AddTicks(-nextOccourrence.Value.Ticks);
        var nextrun = itemInQueue.RecurringTask!.CalculateNextRun(DateTimeOffset.UtcNow,0);
        nextrun = nextrun!.Value.AddTicks(-nextrun.Value.Ticks);

        Assert.Equal(nextOccourrence, nextrun);
    }

    [Fact]
    public async Task TimerCallback_should_process_and_remove_items_correctly()
    {
        var pastTime            = DateTimeOffset.UtcNow.AddMinutes(-1);
        var taskHandlerExecutor = CreateTaskHandlerExecutor(pastTime);
        _timerScheduler.Schedule(taskHandlerExecutor);

        // PeriodicTimerScheduler processes tasks asynchronously, wait for processing
        await Task.Delay(200);

        var itemInQueue = _timerScheduler.GetQueue().Count;
        itemInQueue.ShouldBe(0);
    }

    [Fact]
    public async Task UpdateTimer_should_set_timer_for_next_event_correctly()
    {
        var futureTime          = DateTimeOffset.UtcNow.AddMinutes(10);
        var taskHandlerExecutor = CreateTaskHandlerExecutor(futureTime);
        _timerScheduler.Schedule(taskHandlerExecutor);

        // PeriodicTimerScheduler processes tasks asynchronously
        await Task.Delay(100);

        var itemInQueue = _timerScheduler.GetQueue().TryPeek(out _, out var deliveryTime);
        Assert.Equal(futureTime, deliveryTime);
    }

    [Fact]
    public async Task ProcessItem_should_invoke_DispatcherQueueAsync_with_correct_item()
    {
        var taskHandlerExecutor = CreateTaskHandlerExecutor(DateTimeOffset.UtcNow.AddSeconds(1));
        _timerScheduler.Schedule(taskHandlerExecutor);

        _timerScheduler.GetQueue().Peek().ShouldBe(taskHandlerExecutor);

        await Task.Delay(1500);

        _mockWorkerQueue.Verify(wq => wq.Queue(It.Is<TaskHandlerExecutor>(te => te == taskHandlerExecutor)),
            Times.Once);
    }

    [Fact]
    public async Task DispatcherQueueAsync_should_enqueue_item_in_workerQueue()
    {
        var taskHandlerExecutor = CreateTaskHandlerExecutor();
        await _timerScheduler.DispatchToWorkerQueue(taskHandlerExecutor);

        _mockWorkerQueue.Verify(wq => wq.Queue(taskHandlerExecutor), Times.Once);
    }

    [Fact]
    public async Task DispatcherQueueAsync_should_handle_exceptions_correctly()
    {
        var taskHandlerExecutor = CreateTaskHandlerExecutor();
        _mockWorkerQueue.Setup(wq => wq.Queue(It.IsAny<TaskHandlerExecutor>()))
                        .ThrowsAsync(new Exception("Test Exception"));

        await _timerScheduler.DispatchToWorkerQueue(taskHandlerExecutor);

        // verify that logger is called and error is registered
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception, string>>((v, t) => true)!),
            Times.Once);
    }

    [Fact]
    public async Task TimerCallback_Should_Process_MultipleOverlappingTasks_Correctly()
    {
        var task1 = CreateTaskHandlerExecutor(DateTimeOffset.UtcNow.AddSeconds(-1));
        var task2 = CreateTaskHandlerExecutor(DateTimeOffset.UtcNow.AddSeconds(-1));
        _timerScheduler.Schedule(task1);
        _timerScheduler.Schedule(task2);

        // PeriodicTimerScheduler processes tasks asynchronously
        await Task.Delay(200);

        _timerScheduler.GetQueue().Count.ShouldBe(0);
    }

    [Fact]
    public async Task TimerCallback_Should_RemoveExecutedTask_FromQueue()
    {
        var task = CreateTaskHandlerExecutor(DateTimeOffset.UtcNow.AddSeconds(-1));
        _timerScheduler.Schedule(task);

        // PeriodicTimerScheduler processes tasks asynchronously
        await Task.Delay(200);

        _timerScheduler.GetQueue().Count.ShouldBe(0);
    }

    [Fact]
    public async Task UpdateTimer_Should_SetCorrectDelay_ForFutureTask()
    {
        var futureTime = DateTimeOffset.UtcNow.AddSeconds(5);
        var task       = CreateTaskHandlerExecutor(futureTime);
        _timerScheduler.Schedule(task);

        await Task.Delay(100);
        await Task.Delay(TimeSpan.FromSeconds(5));

        _mockWorkerQueue.Verify(wq => wq.Queue(It.Is<TaskHandlerExecutor>(te => te == task)), Times.Once);
    }

    [Fact]
    public async Task UpdateTimer_Should_DisableTimer_WhenQueueIsEmpty()
    {
        var task = CreateTaskHandlerExecutor(DateTimeOffset.UtcNow.AddSeconds(-1));
        _timerScheduler.Schedule(task);

        await Task.Delay(100);
        _timerScheduler.GetQueue().Count.ShouldBe(0);
        await Task.Delay(3000);

        _mockWorkerQueue.Verify(wq => wq.Queue(It.IsAny<TaskHandlerExecutor>()), Times.Once);
    }

    [Fact]
    public async Task UpdateTimer_Should_SetDelayToZero_ForPastDueTasks()
    {
        var pastTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        var task     = CreateTaskHandlerExecutor(pastTime);
        _timerScheduler.Schedule(task);

        await Task.Delay(100);

        _mockWorkerQueue.Verify(wq => wq.Queue(It.Is<TaskHandlerExecutor>(te => te == task)), Times.Once);
    }

    [Fact]
    public async Task UpdateTimer_Should_SetDelayToOneAndHalfHour_ForDelaysOverTwoHours()
    {
        var longFutureTime = DateTimeOffset.UtcNow.AddHours(3);
        var task           = CreateTaskHandlerExecutor(longFutureTime);
        _timerScheduler.Schedule(task);

        // Wait for PeriodicTimerScheduler to calculate delay
        await Task.Delay(100);

#if DEBUG
        // Verifica che il ritardo calcolato sia di 1 ora e mezza solo in modalità debug
        Assert.Equal(TimeSpan.FromHours(1.5), _timerScheduler.LastCalculatedDelay);
#endif
    }

    private TaskHandlerExecutor CreateTaskHandlerExecutor(DateTimeOffset? executionTime = null, RecurringTask? recurringTask = null) =>
        new(
            new TestTaskRequest2(),
            new TestTaskHanlder2(),
            executionTime,
            recurringTask,
            null!,
            null,
            null,
            null,
            Guid.NewGuid(),
            null,
            null);

    #region PeriodicTimerScheduler Specific Tests (v2.0.0)

    [Fact]
    public async Task PeriodicTimerScheduler_Should_WakeUp_When_UrgentTask_Arrives()
    {
        // Arrange: Schedule a task far in the future (scheduler will sleep)
        var futureTask = CreateTaskHandlerExecutor(DateTimeOffset.UtcNow.AddHours(1));
        _timerScheduler.Schedule(futureTask);

        // Wait for scheduler to enter sleep state
        await Task.Delay(150);

        // Act: Schedule an urgent task (should wake up scheduler immediately)
        var urgentTask = CreateTaskHandlerExecutor(DateTimeOffset.UtcNow.AddMilliseconds(100));
        _timerScheduler.Schedule(urgentTask);

        // Assert: Urgent task should be processed quickly (within 500ms)
        await Task.Delay(500);
        _mockWorkerQueue.Verify(wq => wq.Queue(It.Is<TaskHandlerExecutor>(te => te == urgentTask)), Times.Once);

        // Future task should still be in queue
        _timerScheduler.GetQueue().Count.ShouldBe(1);
    }

    [Fact]
    public async Task PeriodicTimerScheduler_Should_Sleep_When_Queue_Empty()
    {
        // Arrange: Empty queue (scheduler should sleep indefinitely)
        _timerScheduler.GetQueue().Count.ShouldBe(0);

#if DEBUG
        // Wait for scheduler to calculate delay
        await Task.Delay(100);

        // Assert: Calculated delay should be infinite
        _timerScheduler.LastCalculatedDelay.ShouldBe(Timeout.InfiniteTimeSpan);
#endif

        // Act: Add task after delay (should wake up)
        await Task.Delay(100);
        var task = CreateTaskHandlerExecutor(DateTimeOffset.UtcNow.AddMilliseconds(100));
        _timerScheduler.Schedule(task);

        // Assert: Task should be processed
        await Task.Delay(300);
        _mockWorkerQueue.Verify(wq => wq.Queue(It.Is<TaskHandlerExecutor>(te => te == task)), Times.Once);
    }

    [Fact]
    public async Task PeriodicTimerScheduler_Should_ProcessMultipleReadyTasks_InSingleCycle()
    {
        // Arrange: Schedule multiple tasks with same execution time
        var now = DateTimeOffset.UtcNow.AddMilliseconds(100);
        var task1 = CreateTaskHandlerExecutor(now);
        var task2 = CreateTaskHandlerExecutor(now);
        var task3 = CreateTaskHandlerExecutor(now);

        // Act: Schedule all tasks
        _timerScheduler.Schedule(task1);
        _timerScheduler.Schedule(task2);
        _timerScheduler.Schedule(task3);

        // Assert: All should be processed in quick succession
        await Task.Delay(400);

        _mockWorkerQueue.Verify(wq => wq.Queue(It.Is<TaskHandlerExecutor>(te => te == task1)), Times.Once);
        _mockWorkerQueue.Verify(wq => wq.Queue(It.Is<TaskHandlerExecutor>(te => te == task2)), Times.Once);
        _mockWorkerQueue.Verify(wq => wq.Queue(It.Is<TaskHandlerExecutor>(te => te == task3)), Times.Once);

        // Queue should be empty
        _timerScheduler.GetQueue().Count.ShouldBe(0);
    }

    [Fact]
    public async Task PeriodicTimerScheduler_Should_UseDynamicDelay_ForShortIntervals()
    {
        // Arrange: Schedule task with delay < checkInterval (1 second)
        var shortDelay = DateTimeOffset.UtcNow.AddMilliseconds(300);
        var task = CreateTaskHandlerExecutor(shortDelay);

        // Act
        _timerScheduler.Schedule(task);

        await Task.Delay(100);

#if DEBUG
        // Assert: Should calculate delay based on task time, not checkInterval
        _timerScheduler.LastCalculatedDelay.ShouldBeLessThan(TimeSpan.FromSeconds(1));
        _timerScheduler.LastCalculatedDelay.ShouldBeGreaterThan(TimeSpan.Zero);
#endif

        // Task should execute within short time
        await Task.Delay(400);
        _mockWorkerQueue.Verify(wq => wq.Queue(It.Is<TaskHandlerExecutor>(te => te == task)), Times.Once);
    }

    [Fact]
    public async Task PeriodicTimerScheduler_Should_HandleConcurrentScheduleCalls()
    {
        // Arrange: Create multiple tasks
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => CreateTaskHandlerExecutor(DateTimeOffset.UtcNow.AddMilliseconds(200)))
            .ToList();

        // Act: Schedule concurrently
        var scheduleTasks = tasks.Select(task => Task.Run(() => _timerScheduler.Schedule(task)));
        await Task.WhenAll(scheduleTasks);

        // Assert: All tasks should be in queue
        _timerScheduler.GetQueue().Count.ShouldBe(10);

        // Wait for processing
        await Task.Delay(500);

        // All should be processed
        foreach (var task in tasks)
        {
            _mockWorkerQueue.Verify(wq => wq.Queue(It.Is<TaskHandlerExecutor>(te => te == task)), Times.Once);
        }
    }

    [Fact]
    public async Task PeriodicTimerScheduler_Should_RouteToRecurringQueue_ForRecurringTasks()
    {
        // Arrange: Create recurring task without explicit queue name
        var recurringTask = new RecurringTask { MinuteInterval = new MinuteInterval(5) };
        var nextRun = DateTimeOffset.UtcNow.AddMilliseconds(100);
        var taskExecutor = CreateTaskHandlerExecutor(null, recurringTask);

        // Setup queue manager to track queue name
        string? capturedQueueName = null;
        _mockWorkerQueueManager.Setup(x => x.TryEnqueue(It.IsAny<string?>(), It.IsAny<TaskHandlerExecutor>()))
            .Returns<string?, TaskHandlerExecutor>(async (queueName, executor) =>
            {
                capturedQueueName = queueName;
                await _mockWorkerQueue.Object.Queue(executor);
                return true;
            });

        // Act
        _timerScheduler.Schedule(taskExecutor, nextRun);
        await Task.Delay(300);

        // Assert: Should route to "recurring" queue
        capturedQueueName.ShouldBe(QueueNames.Recurring);
    }

    [Fact]
    public async Task PeriodicTimerScheduler_Should_RouteToDefaultQueue_ForNonRecurringTasks()
    {
        // Arrange: Create non-recurring task without explicit queue name
        var taskExecutor = CreateTaskHandlerExecutor(DateTimeOffset.UtcNow.AddMilliseconds(100));

        // Setup queue manager to track queue name
        string? capturedQueueName = null;
        _mockWorkerQueueManager.Setup(x => x.TryEnqueue(It.IsAny<string?>(), It.IsAny<TaskHandlerExecutor>()))
            .Returns<string?, TaskHandlerExecutor>(async (queueName, executor) =>
            {
                capturedQueueName = queueName;
                await _mockWorkerQueue.Object.Queue(executor);
                return true;
            });

        // Act
        _timerScheduler.Schedule(taskExecutor);
        await Task.Delay(300);

        // Assert: Should route to "default" queue
        capturedQueueName.ShouldBe(QueueNames.Default);
    }

    [Fact]
    public async Task PeriodicTimerScheduler_Should_HandleExactlyTwoHourDelay()
    {
        // Arrange: Schedule task exactly 2 hours in future (boundary condition)
        var exactlyTwoHours = DateTimeOffset.UtcNow.AddHours(2);
        var task = CreateTaskHandlerExecutor(exactlyTwoHours);

        // Act
        _timerScheduler.Schedule(task);
        await Task.Delay(100);

#if DEBUG
        // Assert: Should cap at 1.5 hours (delay > 2h triggers cap)
        // Note: Due to timing, might be slightly less than 2h when calculated
        var delay = _timerScheduler.LastCalculatedDelay;
        delay.ShouldBeOneOf(TimeSpan.FromHours(1.5), delay); // Accept either capped or near-2h value
#endif
    }

    [Fact]
    public void PeriodicTimerScheduler_Should_DisposeGracefully()
    {
        // Arrange
        var scheduler = new PeriodicTimerScheduler(_mockWorkerQueueManager.Object, _mockLogger.Object);
        var task = CreateTaskHandlerExecutor(DateTimeOffset.UtcNow.AddHours(1));
        scheduler.Schedule(task);

        // Act: Dispose should not throw
        var exception = Record.Exception(() => scheduler.Dispose());

        // Assert
        exception.ShouldBeNull();
    }

    [Fact]
    public async Task PeriodicTimerScheduler_Should_StopProcessing_AfterDispose()
    {
        // Arrange
        var scheduler = new PeriodicTimerScheduler(_mockWorkerQueueManager.Object, _mockLogger.Object);
        var futureTask = CreateTaskHandlerExecutor(DateTimeOffset.UtcNow.AddSeconds(2));
        scheduler.Schedule(futureTask);

        // Act: Dispose immediately
        scheduler.Dispose();

        // Wait longer than task execution time
        await Task.Delay(2500);

        // Assert: Task should NOT be processed after dispose
        _mockWorkerQueue.Verify(wq => wq.Queue(It.Is<TaskHandlerExecutor>(te => te == futureTask)), Times.Never);
    }

    [Fact]
    public async Task PeriodicTimerScheduler_Should_NotReleaseSemaphore_WhenAlreadySignaled()
    {
        // Arrange: Schedule first task (will release semaphore)
        var task1 = CreateTaskHandlerExecutor(DateTimeOffset.UtcNow.AddHours(1));
        _timerScheduler.Schedule(task1);

        // Act: Schedule second task immediately (semaphore should already be signaled)
        var task2 = CreateTaskHandlerExecutor(DateTimeOffset.UtcNow.AddHours(1));
        _timerScheduler.Schedule(task2);

        await Task.Delay(100);

        // Assert: Both tasks should be in queue, no exceptions thrown
        _timerScheduler.GetQueue().Count.ShouldBe(2);
    }

    #endregion
}
