# EverTask.Logging.Serilog

## Purpose

Serilog integration for `IEverTaskLogger<T>`. Enables structured logging with rich context for task execution events.

**Target Frameworks**: net6.0, net7.0, net8.0, net9.0

## Configuration

```csharp
// Basic (Console sink)
.AddSerilog()

// Custom sinks
.AddSerilog(config => config
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File("logs/evertask-.txt", rollingInterval: RollingInterval.Day)
    .Enrich.FromLogContext())

// From appsettings.json
.AddSerilog(config => config.ReadFrom.Configuration(
    builder.Configuration,
    new ConfigurationReaderOptions { SectionName = "EverTaskSerilog" }))
```

**Effect**: Replaces default `EverTaskLogger<T>` (Microsoft.Extensions.Logging) with Serilog-backed implementation.

## LogLevel Mapping

| Microsoft.Extensions.Logging | Serilog |
|------------------------------|---------|
| `Trace` | `Verbose` |
| `Debug` | `Debug` |
| `Information` | `Information` |
| `Warning` | `Warning` |
| `Error` | `Error` |
| `Critical` | `Fatal` |
| `None` | `Verbose` |

## Message Templates

**Current EverTask Format** (positional):
```csharp
logger.LogInformation("Task {0} started", taskId);
```

**Serilog Best Practice** (named placeholders, recommended for new code):
```csharp
// Before (positional - still works)
logger.LogInformation("Task {0} completed in {1}ms", taskId, duration);

// After (named - better structured logging)
logger.LogInformation("Task {TaskId} completed in {Duration}ms", taskId, duration);
```

Both work, but named placeholders provide better query/filtering in log aggregation tools (Seq, Elasticsearch, Application Insights).

## Scope Enrichment

`BeginScope()` converts `IEnumerable<KeyValuePair<string, object>>` to Serilog enrichers via `LogContext.Push()`:

```csharp
using (logger.BeginScope(new Dictionary<string, object> { ["TaskType"] = typeof(MyTask).Name }))
{
    logger.LogInformation("Processing task");  // Includes TaskType property
}
```

**Built-in Enrichers**: Configure via appsettings.json or code:
- `Enrich.FromLogContext()` — Scope-based enrichment
- `Enrich.WithMachineName()` — Add machine name
- `Enrich.WithThreadId()` — Add thread ID

## 🔗 Test Coverage

**Location**: `test/EverTask.Tests.Logging/Serilog/`

**When adding new logger integrations** (e.g., NLog):
- Duplicate folder structure: `test/EverTask.Tests.Logging/NLog/`
- Follow same test patterns: `ServiceRegistrationTests.cs`, `{Provider}LoggerTests.cs`

**When modifying Serilog integration**:
- Update: `test/EverTask.Tests.Logging/Serilog/SerilogLoggerTests.cs`
- Verify DI registration: `test/EverTask.Tests.Logging/Serilog/ServiceRegistrationTests.cs`

**Test Pattern**: `DelegateSink` for inline assertions (no external output needed).
