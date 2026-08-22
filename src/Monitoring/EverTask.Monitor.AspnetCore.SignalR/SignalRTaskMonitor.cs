using EverTask.Logger;
using EverTask.Monitoring;
using EverTask.Worker;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace EverTask.Monitor.AspnetCore.SignalR;

public class SignalRTaskMonitor(IEverTaskWorkerExecutor executor, IHubContext<TaskMonitorHub> hubContext,
                               IEverTaskLogger<SignalRTaskMonitor> logger,
                               IOptions<SignalRMonitoringOptions> options) : ITaskMonitor
{
    private readonly SignalRMonitoringOptions _options = options.Value;

    public void SubScribe()
    {
        logger.MonitorHubSubscribed();
        executor.TaskEventOccurredAsync += OnTaskEventOccurredAsync;
    }

    private async Task OnTaskEventOccurredAsync(EverTaskEventData eventData)
    {
        logger.MonitorHubEventReceived(eventData.TaskId, eventData.Severity);

        // Filter execution logs based on configuration
        var filteredEventData = _options.IncludeExecutionLogs
            ? eventData // Send with logs
            : eventData with { ExecutionLogs = null }; // Strip logs for network efficiency

        await hubContext.Clients.All.SendAsync("EverTaskEvent", filteredEventData).ConfigureAwait(false);
    }

    public void Unsubscribe()
    {
        logger.MonitorHubUnsubscribed();
        executor.TaskEventOccurredAsync -= OnTaskEventOccurredAsync;
    }

    protected void Dispose(bool disposing)
    {
        Unsubscribe();
    }
}
