using EverTask.Logger;
using EverTask.Monitoring;
using EverTask.Worker;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace EverTask.Monitor.AspnetCore.SignalR;

public class SignalRTaskMonitorHub : Hub, ITaskMonitor
{
    private readonly WorkerService _workerService;
    private readonly IEverTaskLogger<SignalRTaskMonitorHub> _logger;

    public SignalRTaskMonitorHub(WorkerService workerService ,IEverTaskLogger<SignalRTaskMonitorHub> logger)
    {
        _workerService = workerService;
        _logger   = logger;
        SubScribe();
    }

    public void SubScribe()
    {
        _logger.LogInformation("EverTask SignalR MonitorHub created and subscribed");
        _workerService.TaskEventOccurredAsync += OnTaskEventOccurredAsync;
    }

    private async Task OnTaskEventOccurredAsync(EverTaskEventData eventData)
    {
        _logger.LogInformation("EverTask SignalR MonitorHub, message received: {@eventData}", eventData);
        await Clients.All.SendAsync("EverTaskEvent", eventData).ConfigureAwait(false);
    }

    public void Unsubscribe()
    {
        _logger.LogInformation("EverTask SignalR MonitorHub unsubscribed");
        _workerService.TaskEventOccurredAsync -= OnTaskEventOccurredAsync;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Unsubscribe();
    }
}
