# EverTask.Monitor.AspnetCore.SignalR

## Purpose

Real-time task monitoring via SignalR. Broadcasts task lifecycle events (started, completed, error) to connected clients.

**Target Frameworks**: net6.0, net7.0, net8.0, net9.0

## Event Subscription Flow

1. `AddSignalRMonitoring()` → Registers `SignalRTaskMonitor` as `ITaskMonitor` singleton
2. **CRITICAL**: `MapEverTaskMonitorHub()` → Retrieves monitor + calls `SubScribe()`
3. `SubScribe()` → Attaches handler to `IEverTaskWorkerExecutor.TaskEventOccurredAsync`
4. `WorkerExecutor` → Publishes events during task execution
5. SignalR → Broadcasts via `Clients.All.SendAsync("EverTaskEvent", eventData)`

**CRITICAL GOTCHA**: `MapEverTaskMonitorHub()` MUST be called AFTER `app` is built but BEFORE `app.Run()`. This triggers event subscription.

## DI Registration

```csharp
// Service registration
builder.Services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddSignalRMonitoring();

// Hub mapping (REQUIRED)
app.MapEverTaskMonitorHub();              // Default: /evertask/monitor
app.MapEverTaskMonitorHub("/custom/monitor"); // Custom endpoint
```

## Event Payload: EverTaskEventData

```csharp
public record EverTaskEventData(
    Guid TaskId,                // Unique task persistence ID
    DateTimeOffset EventDateUtc, // Event timestamp
    string Severity,            // "Information" | "Warning" | "Error"
    string TaskType,            // Task request type
    string TaskHandlerType,     // Handler type
    string TaskParameters,      // JSON-serialized task request
    string Message,             // Human-readable message
    string? Exception           // Detailed exception (null if no error)
);
```

## Client-Side Integration

**JavaScript/TypeScript**:
```javascript
import * as signalR from "@microsoft/signalr";

const connection = new signalR.HubConnectionBuilder()
    .withUrl("/evertask/monitor")
    .withAutomaticReconnect()
    .build();

connection.on("EverTaskEvent", (eventData) => {
    console.log("Task Event:", eventData);
});

await connection.start();
```

**Health Check** (PowerShell/Bash):
```powershell
# PowerShell
Invoke-WebRequest -Uri "https://localhost:5001/evertask/monitor" -Method HEAD

# Bash/curl
curl -I https://localhost:5001/evertask/monitor
```

Expected: HTTP 200 (SignalR negotiation endpoint active).

## Hub Design

**Server-to-Client Only**: No client-to-server RPC methods.

**No Groups/Filtering**: Uses `Clients.All` — all connected clients receive all events.

## Common Pitfalls

| Issue | Solution |
|-------|----------|
| **Forgot `MapEverTaskMonitorHub()`** | Monitor registered but never subscribes. Verify `MapEverTaskMonitorHub()` called in Program.cs |
| **CORS Issues** | SignalR requires proper CORS config for cross-origin clients. Add `.AddCors()` + `.UseCors()` |
| **WebSocket Support** | Ensure hosting environment supports WebSockets (IIS: enable WebSocket protocol feature) |
| **Event Flood** | High task volume can overwhelm clients. Add client-side throttling (e.g., rxjs `throttleTime`) |
| **Partial Event Visibility (multi-server)** | No backplane = each server broadcasts only its own events. See Scalability below |

## Scalability: Multi-Server Backplane

For distributed deployments, configure a backplane:

**Azure SignalR Service** (recommended):
```csharp
builder.Services.AddSignalR().AddAzureSignalR(connectionString);
```

**Redis Backplane**:
```csharp
builder.Services.AddSignalR().AddStackExchangeRedis(connectionString);
```

**SQL Server Backplane** (not recommended for high-throughput):
```csharp
builder.Services.AddSignalR().AddSqlServer(connectionString);
```

**Config in appsettings.json**:
```json
{
  "Azure": {
    "SignalR": {
      "ConnectionString": "Endpoint=https://...;AccessKey=...;"
    }
  }
}
```

## 🔗 Test Coverage

**Location**: `test/EverTask.Tests.Monitoring/SignalR/SignalRTaskMonitorTests.cs`

**When modifying SignalR integration**:
- Verify subscription flow: `test/EverTask.Tests.Monitoring/SignalR/SignalRTaskMonitorTests.cs`
- Check event broadcasting: `Should_broadcast_task_started_event`, `Should_broadcast_task_completed_event`
