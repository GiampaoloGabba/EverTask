using EverTask.Abstractions;
using EverTask.Worker;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace EverTask.Example.AspnetCore.Controllers;

[ApiController]
[Route("[controller]")]
public class EverTaskTestController : ControllerBase
{
    private readonly ITaskDispatcher _dispatcher;
    private readonly IEverTaskWorkerExecutor _executor;
    private readonly ILogger<EverTaskTestController> _logger;

    public EverTaskTestController(ITaskDispatcher dispatcher, IEverTaskWorkerExecutor executor,
                                  ILogger<EverTaskTestController> logger)
    {
        _dispatcher = dispatcher;
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
        Description = "Schedules a cleanup task to run every minute in the recurring queue")
    ]
    [HttpGet("schedule-cleanup")]
    public async Task<IActionResult> ScheduleCleanup()
    {
        var taskId = await _dispatcher.Dispatch(
            new CleanupExpiredDataTask(),
            recurring => recurring.Schedule().EveryMinute());

        return Ok(new
        {
            message = "Cleanup task scheduled to run every minute in the recurring queue",
            taskId
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
