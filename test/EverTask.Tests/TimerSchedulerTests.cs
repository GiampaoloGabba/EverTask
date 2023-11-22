using Cronos;
using EverTask.Handler;
using EverTask.Logger;
using EverTask.Scheduler;
using EverTask.Scheduler.Recurring;
using Microsoft.Extensions.Logging;

namespace EverTask.Tests;

public class TimerSchedulerTests
{
    private readonly Mock<IWorkerQueue> _mockWorkerQueue;
    private readonly Mock<IEverTaskLogger<TimerScheduler>> _mockLogger;
    private readonly TimerScheduler _timerScheduler;

    public TimerSchedulerTests()
    {
        _mockWorkerQueue = new Mock<IWorkerQueue>();
        _mockLogger      = new Mock<IEverTaskLogger<TimerScheduler>>();
        _timerScheduler  = new TimerScheduler(_mockWorkerQueue.Object, _mockLogger.Object);
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
    public void Schedule_should_enqueue_recurring_item_with_correct_execution_time()
    {
        var cronExpresison      = "*/5 * * * *";
        var nextOccourrence = CronExpression.Parse(cronExpresison)
                                            .GetNextOccurrence(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        var recurringTask       = new RecurringTask { CronExpression = cronExpresison };
        var taskHandlerExecutor = CreateTaskHandlerExecutor(null, recurringTask);

        _timerScheduler.Schedule(taskHandlerExecutor, nextOccourrence);

        var itemInQueue = _timerScheduler.GetQueue().Dequeue();

        Assert.Equal(nextOccourrence, itemInQueue.RecurringTask!.CalculateNextRun(DateTimeOffset.UtcNow,0));
    }

    [Fact]
    public void TimerCallback_should_process_and_remove_items_correctly()
    {
        var pastTime            = DateTimeOffset.UtcNow.AddMinutes(-1);
        var taskHandlerExecutor = CreateTaskHandlerExecutor(pastTime);
        _timerScheduler.Schedule(taskHandlerExecutor);

        _timerScheduler.TimerCallback(null);

        var itemInQueue = _timerScheduler.GetQueue().Count;
        itemInQueue.ShouldBe(0);
    }

    [Fact]
    public void UpdateTimer_should_set_timer_for_next_event_correctly()
    {
        var futureTime          = DateTimeOffset.UtcNow.AddMinutes(10);
        var taskHandlerExecutor = CreateTaskHandlerExecutor(futureTime);
        _timerScheduler.Schedule(taskHandlerExecutor);

        _timerScheduler.TimerCallback(null);

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
            Guid.NewGuid());
}
