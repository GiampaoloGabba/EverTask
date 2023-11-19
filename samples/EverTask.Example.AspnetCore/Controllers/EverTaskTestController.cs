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
    private readonly IEverTaskWorkerService _workerService;
    private readonly ILogger<EverTaskTestController> _logger;

    public EverTaskTestController(ITaskDispatcher dispatcher, IEverTaskWorkerService workerService,
                                  ILogger<EverTaskTestController> logger)
    {
        _dispatcher       = dispatcher;
        _workerService    = workerService;
        _logger           = logger;

        _workerService.TaskEventOccurredAsync += data =>
        {
            _logger.LogInformation("Message received from EverTask Worker Server: {@eventData}", data);
            return Task.CompletedTask;
        };
    }

    [SwaggerOperation(Summary="Read your console for task results", Description = "To check signalr messages, use a client like https://gourav-d.github.io/SignalR-Web-Client/dist/")]
    [HttpGet("send-task")]
    public async Task<IActionResult> SendTask()
    {
        await _dispatcher.Dispatch(new SampleTaskRequest("Hello World"));
        return Content("Task dispatched");
    }
}
