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

    /// <summary>The key of the durable nightly reconciliation, the schedule that replays what it missed.</summary>
    private const string ReconciliationKey = "nightly-reconciliation";

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
        Summary = "Schedule the nightly reconciliation with durable occurrences and a 92-day catch-up",
        Description = "02:00 Europe/Rome every night. Every due slot becomes its OWN row, so a downtime is " +
                      "replayed one night at a time instead of being lost: up to 92 days back and 200 " +
                      "occurrences per episode, two at a time. Watch the logs — the handler reads the day to " +
                      "reconcile from Context.ScheduledAtLocal, never from the clock.")
    ]
    [HttpGet("schedule-reconciliation")]
    public async Task<IActionResult> ScheduleReconciliation()
    {
        var taskId = await _dispatcher.Dispatch(
            new NightlyReconciliationTask(),
            recurring => recurring.Schedule()
                                  .EveryDay().AtTime(new TimeOnly(2, 0))
                                  .InTimeZone("Europe/Rome")
                                  .OnMisfire(m => m.CatchUp(
                                      new CatchUpOptions(TimeSpan.FromDays(92), maxOccurrences: 200)
                                      {
                                          // Two nights may be replayed at once; the rest wait their turn.
                                          MaxPendingOccurrences = 2
                                      })),
            taskKey: ReconciliationKey);

        return Ok(new
        {
            message = "Nightly reconciliation scheduled for 02:00 Europe/Rome, catching up to 92 days",
            taskId
        });
    }

    [SwaggerOperation(
        Summary = "Replay the nightly reconciliation from a past date",
        Description = "Registers a SEPARATE reconciliation series whose cursor starts in the past, so the " +
                      "catch-up has a real backlog: one occurrence row per missed night, visible in the " +
                      "dashboard under the schedule's Occurrences tab, and the series ends once they have " +
                      "all run. BackfillFrom decides where a series STARTS, so it only applies to a first " +
                      "registration: re-registering the nightly key would keep the cursor that row already " +
                      "carries — that is what makes registration idempotent — and nothing would be replayed.")
    ]
    [HttpPost("backfill-reconciliation")]
    public async Task<IActionResult> BackfillReconciliation([FromQuery] int daysBack = 5)
    {
        // A replay of no nights is not a schedule: the run budget below would be refused at build time.
        daysBack = Math.Clamp(daysBack, 1, 90);

        var from = DateTimeOffset.UtcNow.AddDays(-daysBack);

        // One key per replay start, so asking for a different backlog really registers a different series and
        // asking twice for the same one is the no-op an idempotent registration should be.
        var taskKey = $"{ReconciliationKey}:replay:{from:yyyyMMdd}";

        var taskId = await _dispatcher.Dispatch(
            new NightlyReconciliationTask(),
            recurring => recurring.Schedule()
                                  .EveryDay().AtTime(new TimeOnly(2, 0))
                                  .InTimeZone("Europe/Rome")
                                  .OnMisfire(m => m.CatchUp(
                                      new CatchUpOptions(TimeSpan.FromDays(92), maxOccurrences: 200)
                                      {
                                          MaxPendingOccurrences = 2
                                      }))
                                  .BackfillFrom(from)
                                  // The replay is all this series exists for: it ends with the nights it owed
                                  // instead of becoming a second nightly schedule beside the real one.
                                  .MaxRuns(daysBack),
            taskKey: taskKey);

        return Ok(new
        {
            message = $"Reconciliation replay registered with its cursor at {from:u}: the missed nights become occurrences",
            taskId,
            taskKey
        });
    }

    [SwaggerOperation(
        Summary = "Resume the nightly reconciliation after its catch-up halted",
        Description = "A catch-up whose backlog exceeds the configured cap stops itself and stays stopped — " +
                      "not even a restart releases it. This is the explicit operator action that does.")
    ]
    [HttpPost("resume-reconciliation")]
    public async Task<IActionResult> ResumeReconciliation()
    {
        var result = await _schedules.ResumeSchedule(ReconciliationKey);

        return Ok(new
        {
            message = result.ReleasedHalt
                          ? "The catch-up halt was released and the backlog was re-planned"
                          : "The schedule was not halted; it was simply re-evaluated",
            result.TaskId,
            result.ScheduleVersion,
            nextRun = result.NextRunUtc
        });
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
