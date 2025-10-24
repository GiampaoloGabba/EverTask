# EverTask.Tests.Logging

## Purpose

Integration tests for EverTask logging integrations. Currently: Serilog. Future: NLog, etc.

## Test Organization

```
test/EverTask.Tests.Logging/
├── Serilog/
│   ├── ServiceRegistrationTests.cs  # DI registration and resolution
│   └── SerilogLoggerTests.cs        # Logger behavior and functionality
└── GlobalUsings.cs
```

**IMPORTANT**: This project uses **xUnit** (NOT MSTest) with Shouldly assertions.

## Quick Commands

**Run all logging tests**:
```bash
dotnet test test/EverTask.Tests.Logging/EverTask.Tests.Logging.csproj
```

**Run Serilog tests only**:
```bash
dotnet test test/EverTask.Tests.Logging/ --filter "FullyQualifiedName~Serilog"
```

## Key Test Patterns

### DelegateSink Pattern

Custom `ILogEventSink` for inline assertions without external output:

```csharp
var logger = new LoggerConfiguration()
    .WriteTo.Sink(new DelegateSink(logEvent => {
        logEvent.MessageTemplate.Text.ShouldBe("Expected message");
        logEvent.Level.ShouldBe(LogEventLevel.Error);
    }))
    .CreateLogger();

logger.Error("Expected message");
```

**Purpose**: Capture and assert on `LogEvent` properties (level, message, properties, exceptions) without file/console output.

### LogLevel Verification

```csharp
var everTaskLogger = new EverTaskSerilogLogger<MyClass>(logger);

everTaskLogger.IsEnabled(LogLevel.Information).ShouldBeTrue();
everTaskLogger.IsEnabled(LogLevel.None).ShouldBeFalse();
```

### Scope Enrichment Testing

```csharp
using (logger.BeginScope(new Dictionary<string, object> { ["Key"] = "Value" }))
{
    logger.LogInformation("Message");
    // Assert enricher properties via DelegateSink
}
```

## Adding New Logger Integration

**When adding new logger provider** (e.g., NLog):

- [ ] Create folder: `test/EverTask.Tests.Logging/NLog/`
- [ ] Add: `NLog/ServiceRegistrationTests.cs` (verify DI registration)
- [ ] Add: `NLog/NLogLoggerTests.cs` (verify logger behavior)
- [ ] Follow Serilog test patterns (DelegateSink equivalent, LogLevel mapping, scope enrichment)

**Folder structure should mirror Serilog**:
```
test/EverTask.Tests.Logging/
├── Serilog/
│   ├── ServiceRegistrationTests.cs
│   └── SerilogLoggerTests.cs
├── NLog/
│   ├── ServiceRegistrationTests.cs
│   └── NLogLoggerTests.cs
```

## Test Configuration

**Disabling noisy console output** (optional, for cleaner test runs):
```csharp
// In test class constructor or setup
var logger = new LoggerConfiguration()
    .MinimumLevel.Fatal()  // Suppress all output below Fatal
    .CreateLogger();
```

**When testing specific log levels**, configure minimum level accordingly:
```csharp
.MinimumLevel.Verbose()  // Capture all levels for testing
```

## Key Assertions

| Assertion | Example |
|-----------|---------|
| **LogLevel mapping** | `logEvent.Level.ShouldBe(LogEventLevel.Error)` |
| **Message template** | `logEvent.MessageTemplate.Text.ShouldBe("Task {TaskId} started")` |
| **Message properties** | `logEvent.Properties["TaskId"].ToString().ShouldBe(taskId.ToString())` |
| **Exception** | `logEvent.Exception.ShouldBeOfType<InvalidOperationException>()` |
| **IsEnabled** | `logger.IsEnabled(LogLevel.Information).ShouldBeTrue()` |
