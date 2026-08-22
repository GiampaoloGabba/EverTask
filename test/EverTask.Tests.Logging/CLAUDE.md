# EverTask.Tests.Logging

## Purpose

Integration tests for EverTask logging integrations. Currently Serilog; future providers mirror the layout.

```
test/EverTask.Tests.Logging/
└── Serilog/
    ├── ServiceRegistrationTests.cs   # DI registration and resolution
    └── SerilogLoggerTests.cs         # Adapter behavior + the DelegateSink helper
```

xUnit + Shouldly (NOT MSTest). Multi-targets net8.0/net9.0/net10.0, so every assertion must hold on all
three Serilog / `Serilog.Extensions.Logging` versions (8.0.0 / 9.0.0 / 10.0.0).

```bash
dotnet test test/EverTask.Tests.Logging/EverTask.Tests.Logging.csproj -c Release
```

## DelegateSink: capture, then assert AFTER the call

```csharp
var events = new List<LogEvent>();
var serilog = new LoggerConfiguration()
              .MinimumLevel.Is(minimumLevel)
              .WriteTo.Sink(new DelegateSink(events.Add))
              .CreateLogger();

var logger = new EverTaskSerilogLogger<MyClass>(serilog);
logger.LogInformation("Task {TaskId} done", id);

var logEvent = events.ShouldHaveSingleItem();
logEvent.MessageTemplate.Text.ShouldBe("Task {TaskId} done");
```

**Never assert inside the sink delegate.** The assertions then only run if an event is emitted, so the test
passes vacuously when the adapter logs nothing — exactly how the structural-property loss went unnoticed.
`SerilogLoggerTests.CreateLogger` is the helper; reuse it.

## What to assert on a `LogEvent`

| Concern | Assertion |
|---------|-----------|
| Level | `logEvent.Level.ShouldBe(LogEventLevel.Error)` |
| Template (**not** the rendered text) | `logEvent.MessageTemplate.Text.ShouldBe("Task {TaskId} started")` |
| Bound property | `logEvent.Properties["TaskId"].ShouldBeOfType<ScalarValue>().Value.ShouldBe(id)` |
| Absent property | `logEvent.Properties.ContainsKey("X").ShouldBeFalse()` — `Properties` is an `IReadOnlyDictionary`, so Shouldly's `ShouldNotContainKey` does not bind |
| Rendered output | `logEvent.RenderMessage().ShouldBe("Task 42 started")` |
| Exception | `logEvent.Exception.ShouldBeSameAs(expected)` |
| `EventId` | `Properties["EventId"]` is a `StructureValue` with `Id` / `Name` sub-properties |
| Source context | `Properties[Constants.SourceContextPropertyName]` == `typeof(T).FullName` |
| `IsEnabled` | `logger.IsEnabled(LogLevel.None).ShouldBeFalse()` |

## `[LoggerMessage]` coverage

`TestLog` (in `SerilogLoggerTests.cs`) is a source-generated logging class used to prove the adapter handles
the generator's state struct, not just `FormattedLogValues`. The generator needs a **direct**
`Microsoft.Extensions.Logging.Abstractions` PackageReference in this csproj — analyzer assets are private by
default and do not flow in through the project reference.

## Adding a new logger integration

Create `test/EverTask.Tests.Logging/<Provider>/` with `ServiceRegistrationTests.cs` (DI resolves
`IEverTaskLogger<T>` to the provider's type) and `<Provider>LoggerTests.cs` covering the same matrix above.
