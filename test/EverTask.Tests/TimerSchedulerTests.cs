using System.Reflection;
using Cronos;
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
}
