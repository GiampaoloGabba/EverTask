using EverTask.Logger;
using EverTask.Monitoring;
using EverTask.Worker;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace EverTask.Monitor.AspnetCore.SignalR;

public class SignalRTaskMonitor : ITaskMonitor
{
    private readonly IEverTaskWorkerService _workerService;
    private readonly IHubContext<TaskMonitorHub> _hubContext;
    private readonly IEverTaskLogger<SignalRTaskMonitor> _logger;

    public SignalRTaskMonitor(IEverTaskWorkerService workerService, IHubContext<TaskMonitorHub> hubContext,
                              IEverTaskLogger<SignalRTaskMonitor> logger)
    {
        _workerService = workerService;
        _hubContext    = hubContext;
        _logger        = logger;
    }

    public void SubScribe()
    {
        _logger.LogInformation("EverTask SignalR MonitorHub created and subscribed");
        _workerService.TaskEventOccurredAsync += OnTaskEventOccurredAsync;
    }

    private async Task OnTaskEventOccurredAsync(EverTaskEventData eventData)
    {
        _logger.LogInformation("EverTask SignalR MonitorHub, message received: {@eventData}", eventData);
        await _hubContext.Clients.All.SendAsync("EverTaskEvent", eventData).ConfigureAwait(false);
    }

    public void Unsubscribe()
    {
        _logger.LogInformation("EverTask SignalR MonitorHub unsubscribed");
        _workerService.TaskEventOccurredAsync -= OnTaskEventOccurredAsync;
    }

    protected void Dispose(bool disposing)
    {
        Unsubscribe();
    }
}
