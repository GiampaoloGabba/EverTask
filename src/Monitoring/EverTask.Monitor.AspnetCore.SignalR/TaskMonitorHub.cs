using Microsoft.AspNetCore.SignalR;

namespace EverTask.Monitor.AspnetCore.SignalR;

public class TaskMonitorHub : Hub
{
    public override Task OnConnectedAsync()
    {
        return base.OnConnectedAsync();
    }
}
