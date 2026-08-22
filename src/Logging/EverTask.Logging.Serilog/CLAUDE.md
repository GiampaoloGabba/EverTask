# EverTask.Logging.Serilog

## Purpose

Gives EverTask a **dedicated** Serilog pipeline (own sinks, own config section) behind `IEverTaskLogger<T>`,
independent of the host's logging. If the host already calls `UseSerilog()`, the default `EverTaskLogger<T>`
routes through `ILoggerFactory` to Serilog with full structure — this package is then unnecessary.

## Configuration

```csharp
.AddSerilog()                                  // Console sink

.AddSerilog(config => config
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File("logs/evertask-.txt", rollingInterval: RollingInterval.Day))

.AddSerilog(config => config.ReadFrom.Configuration(
    builder.Configuration,
    new ConfigurationReaderOptions { SectionName = "EverTaskSerilog" }))
```

Registers the built `Serilog.ILogger` as a singleton (`TryAddSingleton`) and
`IEverTaskLogger<>` → `EverTaskSerilogLogger<>`, replacing the default `EverTaskLogger<T>`.

## Implementation: it is the official bridge, nothing hand-rolled

`EverTaskSerilogLogger<T>` wraps `SerilogLoggerProvider` (package `Serilog.Extensions.Logging`, already in the
dependency closure via `Serilog.Extensions.Hosting`) and forwards `Log` / `IsEnabled` / `BeginScope` to
`provider.CreateLogger(typeof(T).FullName!)`.

**Never re-render the message before handing it to Serilog.** The previous hand-written adapter called
`formatter(state, exception)` and passed the rendered string to `_logger.Write(level, ex, message)`, i.e. as
the *message template*. That discarded `{OriginalFormat}` and every named property, made each event its own
template (thrashing Serilog's `MessageTemplateCache` — one `Parse` per call), and re-parsed `{…}` fragments
appearing inside rendered values. The bridge binds properties **by name**, so it is correct both for
`FormattedLogValues` (classic `LogInformation("… {TaskId} …", id)`) and for the `[LoggerMessage]`
source-generated state struct.

One `SerilogLoggerProvider` **per logger instance**, deliberately: the bridge keeps its scope stack in an
`AsyncLocal` on the *provider*, so this gives each `IEverTaskLogger<T>` its own stack. `IEverTaskLogger<>` is
a singleton, so the cost is one small object per closed generic. See the comment in `EverTaskSerilogLogger.cs`
before changing this.

## What reaches the sink

| Input | Result |
|-------|--------|
| `{OriginalFormat}` in the state | `LogEvent.MessageTemplate` (grouping/queries work) |
| Named placeholders | `LogEvent.Properties["TaskId"]`, … bound by name |
| `@Name` / `$Name` | destructured / stringified property |
| Non-default `EventId` | `Properties["EventId"]` = `{ Id, Name }` structure |
| `typeof(T).FullName` | `Properties["SourceContext"]` |
| A state that is a plain `string` / any non-property-list | template `{State:l}`, `Properties["State"]` = the formatter's output; `RenderMessage()` is the text |

## LogLevel mapping

`Trace`→`Verbose`, `Debug`→`Debug`, `Information`→`Information`, `Warning`→`Warning`, `Error`→`Error`,
`Critical`→`Fatal`. **`LogLevel.None` ⇒ `IsEnabled` is always `false`** (the old adapter mapped it to
`Verbose` and answered `true` whenever Verbose was enabled).

## Scopes — behaviour change vs. the hand-written adapter

```csharp
using (logger.BeginScope(new Dictionary<string, object> { ["TaskKey"] = "order-42" }))
    logger.LogInformation("Processing");     // carries TaskKey
```

Scopes are **no longer pushed into the global `Serilog.Context.LogContext`**. They are attached by the
provider that created the logger, i.e. they enrich the events of *that* `IEverTaskLogger<T>` only — correct
MEL semantics, but:

- `Enrich.FromLogContext()` is no longer required for EverTask scopes to show up (it stays relevant for the
  host's own `LogContext.Push` calls);
- an EverTask scope no longer leaks into unrelated Serilog loggers in the process;
- non-KVP scope states (e.g. `BeginScope("text")`) are no longer silently dropped — they land in the `Scope`
  array instead of being a no-op.

## Test coverage

`test/EverTask.Tests.Logging/Serilog/` — `SerilogLoggerTests.cs` (templates, properties, `EventId`,
`SourceContext`, scopes, `IsEnabled`), `ServiceRegistrationTests.cs` (DI). Adding another provider (NLog, …):
mirror the folder and both files.
