# CLAUDE.md - EverTask.Logging.Serilog

AI coding agent documentation for EverTask's Serilog integration package.

## Project Purpose

Provides Serilog integration for EverTask's custom logging infrastructure (`IEverTaskLogger<T>`). Bridges EverTask's logging abstraction with Serilog's structured logging capabilities, enabling rich contextual logging for task execution events.

## Architecture

### Core Components

**EverTaskSerilogLogger<T>** (`EverTaskSerilogLogger.cs`)
- Adapter implementing `IEverTaskLogger<T>` backed by Serilog
- Wraps `Serilog.ILogger` with context for type `T`
- Converts `Microsoft.Extensions.Logging.LogLevel` to `Serilog.Events.LogEventLevel`
- Handles scope-based enrichment via `LogContext.Push()`

**ServiceCollectionExtensions** (`ServiceCollectionExtensions.cs`)
- Extension method: `AddSerilog(this EverTaskServiceBuilder builder, Action<LoggerConfiguration>? configure = null)`
- Registers `Serilog.ILogger` singleton and `IEverTaskLogger<>` generic implementation
- Default configuration: Console sink if no custom configuration provided

## Integration Points

### EverTask Logger Abstraction

EverTask uses `IEverTaskLogger<T>` (extends `Microsoft.Extensions.Logging.ILogger<T>`) throughout:
- `WorkerExecutor`: Task lifecycle logging (start, completion, errors, cancellation)
- `Dispatcher`: Task dispatch and persistence operations
- `WorkerService`, `WorkerQueue`, `TimerScheduler`, `MemoryTaskStorage`: Component-level logging

### Default vs Serilog Logger

Without `AddSerilog()`:
- EverTask uses `EverTaskLogger<T>` (fallback to `ILoggerFactory`)
- Logs via Microsoft.Extensions.Logging infrastructure

With `AddSerilog()`:
- Replaces generic `IEverTaskLogger<>` registration
- All EverTask components log through Serilog
- Enables structured logging, enrichers, and Serilog sinks

## Configuration

### Basic Registration

```csharp
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddSerilog(); // Default: Console sink
```

### Custom Configuration

```csharp
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddSerilog(config => config
        .MinimumLevel.Information()
        .WriteTo.Console()
        .WriteTo.File("logs/evertask-.txt", rollingInterval: RollingInterval.Day)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithThreadId());
```

### Configuration from appsettings.json

```csharp
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddSerilog(config => config.ReadFrom.Configuration(
        builder.Configuration,
        new ConfigurationReaderOptions { SectionName = "EverTaskSerilog" }));
```

Example `appsettings.json`:
```json
{
  "EverTaskSerilog": {
    "MinimumLevel": { "Default": "Information" },
    "WriteTo": [
      { "Name": "Console" },
      { "Name": "File", "Args": { "path": "Logs/evertask-log-.txt" } }
    ],
    "Enrich": ["FromLogContext", "WithMachineName", "WithThreadId"],
    "Properties": { "Application": "MyApp" }
  }
}
```

## Log Enrichment

### Scope-Based Enrichment

The `BeginScope<TState>` method converts `IEnumerable<KeyValuePair<string, object>>` into Serilog enrichers:

```csharp
// In EverTask code (hypothetical)
using (logger.BeginScope(new Dictionary<string, object>
{
    ["TaskId"] = taskId,
    ["TaskType"] = taskTypeName
}))
{
    logger.LogInformation("Executing task");
    // Logs will include TaskId and TaskType properties
}
```

**Implementation Detail**:
- `LogEventEnricherProperty` inner class wraps key-value pairs as `ILogEventEnricher`
- `LogContext.Push()` returns `IDisposable` for scope cleanup
- Non-dictionary scopes return `NoOpDisposable` (silent no-op)

### Built-in Enrichers

Serilog enrichers (configured in `appsettings.json` or code):
- `FromLogContext`: Enables scope-based properties
- `WithMachineName`: Adds machine name to all logs
- `WithThreadId`: Adds thread ID for concurrent task debugging

## Structured Logging Patterns

### Task Execution Metadata

EverTask components log structured data automatically:

```csharp
// From WorkerExecutor.cs:35
logger.LogInformation("Starting task with id {0}.", task.PersistenceId);

// From WorkerExecutor.cs:51
logger.LogInformation("Task with id {0} was completed in {1} ms.", task.PersistenceId, executionTime);

// From WorkerExecutor.cs:235
logger.LogError(exception, "Error occurred executing task with id {0}.", task.PersistenceId);
```

Serilog captures:
- `PersistenceId` (Guid): Unique task identifier
- `executionTime` (double): Task duration in milliseconds
- `exception`: Full exception details with stack traces

### Log Level Mapping

```csharp
// Microsoft.Extensions.Logging.LogLevel -> Serilog.Events.LogEventLevel
Trace       -> Verbose
Debug       -> Debug
Information -> Information
Warning     -> Warning
Error       -> Error
Critical    -> Fatal
None        -> Verbose
```

### Message Template Compatibility

EverTask uses positional placeholders (`{0}`, `{1}`), not named placeholders (`{TaskId}`). Serilog handles both:
- Positional: `"Task {0} completed in {1} ms"` → Properties: `0="abc"`, `1=123`
- Named (Serilog best practice): `"Task {TaskId} completed in {Duration} ms"` → Properties: `TaskId="abc"`, `Duration=123`

**Note**: To improve structured logging, consider migrating EverTask core to named placeholders in future versions.

## Extension Methods

### Public API Surface

**ServiceCollectionExtensions.AddSerilog()**
```csharp
public static EverTaskServiceBuilder AddSerilog(
    this EverTaskServiceBuilder builder,
    Action<LoggerConfiguration>? configure = null)
```

- **Parameters**:
  - `builder`: EverTask fluent configuration builder (returned by `AddEverTask()`)
  - `configure`: Optional Serilog configuration callback. If `null`, defaults to Console sink.

- **Returns**: `EverTaskServiceBuilder` for method chaining

- **Side Effects**:
  - Registers `Serilog.ILogger` singleton (TryAddSingleton, won't override if already registered)
  - Registers `IEverTaskLogger<>` as `EverTaskSerilogLogger<>` (replaces default implementation)

## Dependencies

From `EverTask.Logging.Serilog.csproj`:
- **Microsoft.Extensions.Configuration.Abstractions**: Configuration integration
- **Microsoft.Extensions.Logging.Abstractions**: `ILogger<T>` interface
- **Serilog.Extensions.Hosting**: DI integration
- **Serilog.Settings.Configuration**: `ReadFrom.Configuration()` support
- **Serilog.Sinks.Console**: Default sink

Additional sinks (e.g., File, Seq, Elasticsearch) must be added to consuming projects.

## Testing

Test coverage in `test/EverTask.Tests.Logging/Serilog/`:

**SerilogLoggerTests.cs**:
- `Should_return_expected_values_for_IsEnabled()`: Verifies log level filtering
- `Should_log_correctly()`: Validates message template and log level conversion using custom `DelegateSink`
- `Should_begin_scope_correctly()`: Ensures scope returns non-null `IDisposable`

**ServiceRegistrationTests.cs**:
- `Should_be_registered_and_resolved_correctly()`: Confirms DI registration resolves `IEverTaskLogger<T>` and `Serilog.ILogger`

**Note**: The test resolves `IEverTaskLogger<T>` to `EverTaskLogger<T>` (default implementation), not `EverTaskSerilogLogger<T>`. This is because `AddSerilog()` uses `AddSingleton()` which doesn't override the default registration. The logger still works because `EverTaskLogger<T>` lazily resolves `ILogger<T>` from the service provider, which uses Serilog under the hood.

## Implementation Notes

### Thread Safety
- `EverTaskSerilogLogger<T>` is thread-safe (Serilog loggers are immutable and thread-safe)
- Singleton `ILogger` instance shared across all task executions
- Scoped enrichers are thread-local via `LogContext.Push()`

### Performance
- `IsEnabled()` check before logging to avoid unnecessary formatting
- Lazy logger creation in default `EverTaskLogger<T>` (not Serilog-specific)
- Serilog's buffered sinks minimize I/O blocking

### Migration from Default Logger
To switch existing EverTask projects to Serilog:
1. Add package: `dotnet add package EverTask.Logging.Serilog`
2. Add sink packages: `dotnet add package Serilog.Sinks.File` (or others)
3. Replace storage registration:
   ```csharp
   // Before
   services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
       .AddMemoryStorage();

   // After
   services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
       .AddMemoryStorage()
       .AddSerilog(config => config.WriteTo.Console().WriteTo.File("logs/evertask-.txt"));
   ```

### Subdirectories
No subdirectories contain source files. All implementation is in root `.cs` files. No additional CLAUDE.md files needed.

## Related Documentation

- Root `CLAUDE.md`: Overall project structure and EverTask architecture
- `src/EverTask/Logger/IEverTaskLogger.cs`: Logger abstraction interface
- `src/EverTask/Worker/WorkerExecutor.cs`: Primary logger consumer (lines 255-318)
