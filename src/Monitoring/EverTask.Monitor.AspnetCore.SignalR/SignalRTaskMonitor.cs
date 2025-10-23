using EverTask.Logger;
using EverTask.Monitoring;
using EverTask.Worker;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EverTask.Monitor.AspnetCore.SignalR;

public class SignalRTaskMonitor : ITaskMonitor
{
    private readonly IEverTaskWorkerExecutor _executor;
    private readonly IHubContext<TaskMonitorHub> _hubContext;
    private readonly IEverTaskLogger<SignalRTaskMonitor> _logger;
    private readonly SignalRMonitoringOptions _options;

    public SignalRTaskMonitor(IEverTaskWorkerExecutor executor, IHubContext<TaskMonitorHub> hubContext,
                              IEverTaskLogger<SignalRTaskMonitor> logger,
                              IOptions<SignalRMonitoringOptions> options)
    {
        _executor   = executor;
        _hubContext = hubContext;
        _logger     = logger;
        _options    = options.Value;
    }

    public void SubScribe()
    {
        _logger.LogInformation("EverTask SignalR MonitorHub created and subscribed");
        _executor.TaskEventOccurredAsync += OnTaskEventOccurredAsync;
    }

    private async Task OnTaskEventOccurredAsync(EverTaskEventData eventData)
    {
        _logger.LogInformation("EverTask SignalR MonitorHub, message received: {@eventData}", eventData);

        // Filter execution logs based on configuration
        var filteredEventData = _options.IncludeExecutionLogs
            ? eventData // Send with logs
            : eventData with { ExecutionLogs = null }; // Strip logs for network efficiency

        await _hubContext.Clients.All.SendAsync("EverTaskEvent", filteredEventData).ConfigureAwait(false);
    }

    public void Unsubscribe()
    {
        _logger.LogInformation("EverTask SignalR MonitorHub unsubscribed");
        _executor.TaskEventOccurredAsync -= OnTaskEventOccurredAsync;
    }

    protected void Dispose(bool disposing)
    {
        Unsubscribe();
    }
}
