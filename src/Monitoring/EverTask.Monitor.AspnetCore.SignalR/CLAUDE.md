# CLAUDE.md - EverTask.Monitor.AspnetCore.SignalR

## Project Purpose

Real-time task monitoring for EverTask via SignalR. Broadcasts task lifecycle events (started, completed, error) to connected clients for building dashboards, monitoring UIs, and observability tools.

## Architecture Overview

### Core Components

**SignalRTaskMonitor** (`SignalRTaskMonitor.cs`)
- Implements `ITaskMonitor` interface
- Subscribes to `IEverTaskWorkerExecutor.TaskEventOccurredAsync` event
- Broadcasts events to all connected SignalR clients via `IHubContext<TaskMonitorHub>`
- Registered as singleton in DI container

**TaskMonitorHub** (`TaskMonitorHub.cs`)
- Minimal SignalR hub for client connections
- No server-side methods exposed to clients
- Acts as broadcast channel only (server-to-client communication)

**Extension Methods**
- `ServiceCollectionExtensions.cs`: DI registration (`AddSignalRMonitoring`)
- `AppBuilderExtensions.cs`: Hub mapping and subscription (`MapEverTaskMonitorHub`)

## Event Subscription Flow

1. `AddSignalRMonitoring()` registers `SignalRTaskMonitor` as `ITaskMonitor` singleton
2. `MapEverTaskMonitorHub()` retrieves the monitor from DI and calls `SubScribe()`
3. `SubScribe()` attaches handler to `IEverTaskWorkerExecutor.TaskEventOccurredAsync`
4. `WorkerExecutor` publishes events via `PublishEvent()` during task execution
5. `OnTaskEventOccurredAsync()` broadcasts to all SignalR clients via `Clients.All.SendAsync("EverTaskEvent", eventData)`

## DI Registration

### Basic Registration
```csharp
builder.Services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddMemoryStorage()
    .AddSignalRMonitoring(); // Registers SignalR and SignalRTaskMonitor
```

### With Hub Configuration
```csharp
builder.Services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddMemoryStorage()
    .AddSignalRMonitoring(hubOptions =>
    {
        hubOptions.EnableDetailedErrors = true;
        hubOptions.MaximumReceiveMessageSize = 128 * 1024; // 128KB
    });
```

### Hub Mapping
```csharp
// Default endpoint: /evertask/monitor
app.MapEverTaskMonitorHub();

// Custom endpoint
app.MapEverTaskMonitorHub("/custom/monitor");

// With connection options (CORS, WebSockets, etc.)
app.MapEverTaskMonitorHub("/evertask/monitor", options =>
{
    options.Transports = HttpTransportType.WebSockets;
});
```

**IMPORTANT**: `MapEverTaskMonitorHub()` must be called AFTER `app` is built but BEFORE `app.Run()`. This triggers the subscription to task events.

## Message Format

### Event Payload: `EverTaskEventData`

Sent via `SendAsync("EverTaskEvent", eventData)` to clients.

```csharp
public record EverTaskEventData(
    Guid TaskId,                // Unique task persistence ID
    DateTimeOffset EventDateUtc, // Event timestamp
    string Severity,            // "Information" | "Warning" | "Error"
    string TaskType,            // Task request type (e.g., "MyApp.Tasks.SendEmailTask")
    string TaskHandlerType,     // Handler type (e.g., "MyApp.Handlers.SendEmailHandler")
    string TaskParameters,      // JSON-serialized task request (Newtonsoft.Json)
    string Message,             // Human-readable message
    string? Exception           // Detailed exception string (null if no error)
);
```

### Severity Levels
- **Information**: Task started, completed successfully
- **Warning**: Retries, timeout warnings (implementation-dependent)
- **Error**: Task failed, unhandled exceptions

## Client-Side Integration

### JavaScript/TypeScript (SignalR Client)

```javascript
import * as signalR from "@microsoft/signalr";

const connection = new signalR.HubConnectionBuilder()
    .withUrl("/evertask/monitor") // Match server endpoint
    .withAutomaticReconnect()
    .configureLogging(signalR.LogLevel.Information)
    .build();

connection.on("EverTaskEvent", (eventData) => {
    console.log("Task Event:", eventData);
    // eventData structure:
    // {
    //   taskId: "guid",
    //   eventDateUtc: "2025-10-18T10:30:00Z",
    //   severity: "Information",
    //   taskType: "MyApp.Tasks.SendEmailTask",
    //   taskHandlerType: "MyApp.Handlers.SendEmailHandler",
    //   taskParameters: "{\"to\":\"user@example.com\"}",
    //   message: "Task started",
    //   exception: null
    // }
});

connection.start()
    .then(() => console.log("Connected to EverTask monitor"))
    .catch(err => console.error("Connection failed:", err));
```

### TypeScript Type Definitions

```typescript
interface EverTaskEventData {
    taskId: string;
    eventDateUtc: string; // ISO 8601 timestamp
    severity: "Information" | "Warning" | "Error";
    taskType: string;
    taskHandlerType: string;
    taskParameters: string; // JSON string
    message: string;
    exception: string | null;
}

connection.on("EverTaskEvent", (eventData: EverTaskEventData) => {
    // Type-safe event handling
});
```

### Client-Side Considerations

- **Automatic Reconnection**: Use `.withAutomaticReconnect()` to handle transient network issues
- **Deserialization**: `taskParameters` is JSON string - parse with `JSON.parse()` if needed
- **Error Handling**: Subscribe to `connection.onclose()` and `connection.onreconnecting()` for robust UIs
- **Authentication**: If hub requires auth, configure `.withUrl()` with access token options

## Hub Design

### Server-to-Client Only
The hub broadcasts events to all clients. No client-to-server RPC methods are implemented.

### No Groups/Filtering
Current implementation uses `Clients.All` - all connected clients receive all events. For selective broadcasting:

```csharp
// Example extension: filter by task type
await _hubContext.Clients.Group("EmailTasks").SendAsync("EverTaskEvent", eventData);
```

Requires clients to join groups via hub method (not implemented by default).

### Hub Lifecycle
- **OnConnectedAsync**: Overridden for extensibility but performs no custom logic
- No authentication/authorization by default - secure via ASP.NET Core policies if needed

## Scalability Considerations

### Connection Management
- SignalR connections are persistent WebSocket/SSE/Long Polling connections
- Each connection consumes server resources (memory, threads)
- Monitor connection count via SignalR metrics

### Backplane Options
For distributed deployments (multiple servers), configure a backplane to synchronize messages:

**Azure SignalR Service** (recommended for production)
```csharp
builder.Services.AddSignalR().AddAzureSignalR(options =>
{
    options.ConnectionString = "Endpoint=...";
});
```

**Redis Backplane**
```csharp
builder.Services.AddSignalR().AddStackExchangeRedis(options =>
{
    options.Configuration.EndPoints.Add("localhost", 6379);
});
```

**SQL Server Backplane** (not recommended for high-throughput)
```csharp
builder.Services.AddSignalR().AddSqlServer(options =>
{
    options.ConnectionString = "...";
});
```

### Performance Tips
- **Message Volume**: High-frequency task executions generate many events. Consider rate-limiting or batching in custom implementations
- **Payload Size**: `taskParameters` serialization can be large for complex tasks. Consider filtering sensitive/large data
- **Client Throttling**: Clients should debounce UI updates to avoid performance degradation

## Logging

SignalRTaskMonitor uses `IEverTaskLogger<SignalRTaskMonitor>` for diagnostics:
- Logs subscription/unsubscription at Information level
- Logs each event received at Information level with structured data (`{@eventData}`)

Configure EverTask-specific logging in `appsettings.json`:
```json
{
  "EverTaskSerilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "EverTask.Monitor.AspnetCore.SignalR": "Debug"
      }
    }
  }
}
```

## Dependencies

- **Microsoft.AspNetCore.App** (framework reference): SignalR, dependency injection
- **EverTask**: Core library for `IEverTaskWorkerExecutor`, `ITaskMonitor`, event types
- **EverTask.Abstractions**: Monitoring interfaces

No external NuGet packages required beyond ASP.NET Core framework.

## Testing

No unit tests in repository for this package. Integration testing requires:
1. ASP.NET Core test host
2. SignalR test client (`HubConnectionBuilder` with in-memory transport)
3. Dispatching tasks and asserting SignalR messages received

Example test structure:
```csharp
var connection = new HubConnectionBuilder()
    .WithUrl("http://localhost/evertask/monitor", options =>
    {
        options.HttpMessageHandlerFactory = _ => server.CreateHandler();
    })
    .Build();

List<EverTaskEventData> events = new();
connection.On<EverTaskEventData>("EverTaskEvent", data => events.Add(data));

await connection.StartAsync();
await dispatcher.DispatchAsync(new MyTask());
await Task.Delay(500); // Allow event propagation

Assert.Single(events);
Assert.Equal("MyTask", events[0].TaskType);
```

## Package Information

- **PackageId**: EverTask.Monitor.AspnetCore.SignalR
- **Target Frameworks**: net6.0, net7.0, net8.0
- **Dependencies**: EverTask (project reference)

## Common Pitfalls

1. **Forgetting to call MapEverTaskMonitorHub()**: Monitor is registered but never subscribes to events
2. **CORS Issues**: SignalR requires proper CORS configuration for cross-origin clients
3. **WebSocket Support**: Ensure hosting environment supports WebSockets (IIS/Kestrel configuration)
4. **Event Flood**: High task volume can overwhelm clients - consider client-side filtering/throttling
5. **No Backplane in Scale-Out**: Multiple servers without backplane results in partial event visibility

## Future Enhancements

Potential improvements not yet implemented:
- Client filtering by task type/severity
- Event batching/compression for high-throughput scenarios
- Hub authentication/authorization hooks
- Metrics endpoint for connection count/event rate
- Client-to-server commands (pause worker, query task status)
