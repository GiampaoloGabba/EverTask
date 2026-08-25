using EverTask.Abstractions;
using EverTask.Worker;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace EverTask.Example.AspnetCore.Controllers;

[ApiController]
[Route("[controller]")]
public class EverTaskTestController : ControllerBase
{
    /// <summary>The key the nightly cleanup is registered under, and the name every schedule call uses.</summary>
    private const string CleanupKey = "nightly-cleanup";

    private readonly ITaskDispatcher _dispatcher;
    private readonly ITaskScheduleManager _schedules;
    private readonly IEverTaskWorkerExecutor _executor;
    private readonly ILogger<EverTaskTestController> _logger;

    public EverTaskTestController(ITaskDispatcher dispatcher, ITaskScheduleManager schedules,
                                  IEverTaskWorkerExecutor executor,
                                  ILogger<EverTaskTestController> logger)
    {
        _dispatcher = dispatcher;
        _schedules  = schedules;
        _executor   = executor;
        _logger     = logger;

        _executor.TaskEventOccurredAsync += data =>
        {
            _logger.LogInformation("Message received from EverTask Worker Server: {@eventData}", data);
            return Task.CompletedTask;
        };
    }

    [SwaggerOperation(
        Summary = "Send a simple task to the default queue",
        Description = "Read your console for task results. To check signalr messages, use a client like https://gourav-d.github.io/SignalR-Web-Client/dist/")
    ]
    [HttpGet("send-task")]
    public async Task<IActionResult> SendTask()
    {
        await _dispatcher.Dispatch(new SampleTaskRequest("Hello World from default queue"));
        return Content("Task dispatched to default queue");
    }

    [SwaggerOperation(
        Summary = "Send a high-priority payment task",
        Description = "Dispatches a payment processing task to the high-priority queue")
    ]
    [HttpGet("send-payment")]
    public async Task<IActionResult> SendPaymentTask()
    {
        var paymentTask = new ProcessPaymentTask(
            Guid.NewGuid(),
            Random.Shared.Next(100, 10000),
            "USD");

        var taskId = await _dispatcher.Dispatch(paymentTask);
        return Ok(new { message = "Payment task dispatched to high-priority queue", taskId });
    }

    [SwaggerOperation(
        Summary = "Send a background report generation task",
        Description = "Dispatches a CPU-intensive report generation task to the background queue")
    ]
    [HttpGet("generate-report")]
    public async Task<IActionResult> GenerateReport()
    {
        var reportTask = new GenerateReportTask(
            "Sales Report",
            DateOnly.FromDateTime(DateTime.Now.AddMonths(-1)),
            DateOnly.FromDateTime(DateTime.Now));

        var taskId = await _dispatcher.Dispatch(reportTask);
        return Ok(new { message = "Report task dispatched to background queue", taskId });
    }

    [SwaggerOperation(
        Summary = "Send multiple tasks to different queues",
        Description = "Demonstrates workload isolation by sending tasks to different queues simultaneously")
    ]
    [HttpGet("send-multiple")]
    public async Task<IActionResult> SendMultipleTasks()
    {
        var tasks = new List<Task<Guid>>();

        // Send tasks to different queues
        tasks.Add(_dispatcher.Dispatch(new ProcessPaymentTask(Guid.NewGuid(), 500, "EUR")));
        tasks.Add(_dispatcher.Dispatch(new SendEmailTask("user@example.com", "Order Confirmation", "Your order has been processed")));
        tasks.Add(_dispatcher.Dispatch(new GenerateReportTask("Inventory Report", DateOnly.FromDateTime(DateTime.Now), DateOnly.FromDateTime(DateTime.Now))));
        tasks.Add(_dispatcher.Dispatch(new ProcessImageTask("/images/product.jpg", "resize")));

        var taskIds = await Task.WhenAll(tasks);

        return Ok(new
        {
            message = "Multiple tasks dispatched to different queues",
            taskIds = taskIds.Select((id, index) => new
            {
                id,
                queue = index switch
                {
                    0 => "high-priority (payment)",
                    1 => "default (email)",
                    2 => "background (report)",
                    3 => "background (image)",
                    _ => "unknown"
                }
            })
        });
    }

    [SwaggerOperation(
        Summary = "Schedule a recurring cleanup task",
        Description = "Schedules a cleanup task to run every night at 03:00 in Rome, under the task key " +
                      "'nightly-cleanup' so a restart updates it instead of duplicating it")
    ]
    [HttpGet("schedule-cleanup")]
    public async Task<IActionResult> ScheduleCleanup()
    {
        var taskId = await _dispatcher.Dispatch(
            new CleanupExpiredDataTask(),
            recurring => recurring.Schedule().EveryDay().AtTime(new TimeOnly(3, 0)).InTimeZone("Europe/Rome"),
            taskKey: CleanupKey);

        return Ok(new
        {
            message = "Cleanup task scheduled for 03:00 Europe/Rome in the recurring queue",
            taskId
        });
    }

    [SwaggerOperation(
        Summary = "Move the nightly cleanup to another hour",
        Description = "The runtime counterpart of the endpoint above: it changes a schedule that is already " +
                      "registered, keeping it on the day it was already on (RebaseFromCursor). A run already " +
                      "in progress is never a reason to refuse — it simply recomputes when it finishes.")
    ]
    [HttpPost("reschedule-cleanup")]
    public async Task<IActionResult> RescheduleCleanup([FromQuery] int hour, [FromQuery] int minute = 0)
    {
        var result = await _schedules.Reschedule(
            CleanupKey,
            recurring => recurring.Schedule()
                                  .EveryDay()
                                  .AtTime(new TimeOnly(hour, minute))
                                  .InTimeZone("Europe/Rome"),
            RescheduleMode.RebaseFromCursor);

        return Ok(new
        {
            message = $"Nightly cleanup moved to {hour:D2}:{minute:D2} Europe/Rome",
            result.TaskId,
            result.ScheduleVersion,
            previousNextRun = result.PreviousNextRunUtc,
            nextRun         = result.NextRunUtc
        });
    }

    [SwaggerOperation(
        Summary = "Cancel the nightly cleanup by its task key",
        Description = "Cancels the schedule and every occurrence of it still pending, without needing the id")
    ]
    [HttpDelete("cancel-cleanup")]
    public async Task<IActionResult> CancelCleanup()
    {
        await _schedules.CancelSchedule(CleanupKey);
        return Ok(new { message = "Nightly cleanup cancelled" });
    }

    [SwaggerOperation(
        Summary = "Cancel a running task",
        Description = "Cancels a task by its ID")
    ]
    [HttpDelete("cancel/{taskId}")]
    public async Task<IActionResult> CancelTask(Guid taskId)
    {
        await _dispatcher.Cancel(taskId);
        return Ok(new { message = $"Task {taskId} cancelled" });
    }
}
