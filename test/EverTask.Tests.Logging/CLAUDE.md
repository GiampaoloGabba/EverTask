# CLAUDE.md - EverTask.Tests.Logging

Test project for EverTask logging integrations. Currently focuses on Serilog integration tests.

## Project Purpose

Validates that logging integrations correctly implement the `IEverTaskLogger<T>` interface and integrate properly with EverTask's dependency injection system. Tests ensure log levels, scopes, and enrichment work as expected.

## Test Organization

### Directory Structure
- `Serilog/ServiceRegistrationTests.cs` - DI registration and resolution tests
- `Serilog/SerilogLoggerTests.cs` - Logger behavior and functionality tests

### Test Categories
1. **Service Registration Tests**: Verify that logging services register and resolve correctly through `AddSerilog()` extension
2. **Logger Functionality Tests**: Validate logging operations (IsEnabled, Log, BeginScope)

## Test Frameworks and Dependencies

- **xUnit**: Primary test framework (not MSTest - note project uses xUnit despite repo standard)
- **Serilog**: The logging library under test
- **Serilog.Sinks**: Custom test sinks for capturing and asserting log events
- **Moq**: Mocking framework (referenced but not actively used in current tests)
- **Shouldly**: Assertion library (referenced but not actively used - tests use xUnit Assert)

## Verification Patterns

### Service Registration Pattern
```csharp
var services = new ServiceCollection();
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Test).Assembly))
        .AddSerilog();
var serviceProvider = services.BuildServiceProvider();
var logger = serviceProvider.GetService<IEverTaskLogger<Test>>();
Assert.NotNull(logger);
Assert.IsType<EverTaskSerilogLogger<Test>>(logger);
```

### Log Event Capture Pattern
Use a custom `DelegateSink` to intercept and assert on log events:
```csharp
var logger = new LoggerConfiguration()
    .WriteTo.Sink(new DelegateSink(logEvent => {
        Assert.Equal("Expected message", logEvent.MessageTemplate.Text);
        Assert.Equal(LogEventLevel.Error, logEvent.Level);
    }))
    .CreateLogger();
```

### Log Level Verification Pattern
```csharp
var logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .CreateLogger();
var everTaskLogger = new EverTaskSerilogLogger<T>(logger);
Assert.True(everTaskLogger.IsEnabled(LogLevel.Information));
Assert.False(everTaskLogger.IsEnabled(LogLevel.None));
```

## Implementation Details

### IEverTaskLogger<T> Interface
Custom logger interface extending `ILogger<T>` from Microsoft.Extensions.Logging. Located at `src/EverTask/Logger/IEverTaskLogger.cs`.

### Two Logger Implementations
1. **EverTaskLogger<T>** (core): Default implementation wrapping ILogger<T> from DI
2. **EverTaskSerilogLogger<T>** (Serilog integration): Direct Serilog integration with:
   - LogLevel to LogEventLevel conversion (Trace->Verbose, Critical->Fatal, etc.)
   - Scope enrichment via `LogContext.Push()` and `ILogEventEnricher`
   - KeyValuePair property extraction for structured logging

### DelegateSink Pattern
`DelegateSink` is a test utility class implementing `ILogEventSink`. It accepts an `Action<LogEvent>` delegate, allowing inline assertions on log events without external file/console output.

## Adding Tests

### Testing a New Logging Integration
1. Create subdirectory under `test/EverTask.Tests.Logging/` (e.g., `NLog/`)
2. Add service registration test verifying DI setup
3. Add functionality tests for:
   - Log level filtering (`IsEnabled`)
   - Message formatting and emission (`Log`)
   - Scope handling (`BeginScope`)
4. Use test sinks/captures specific to the logging library

### Testing Log Enrichment
When testing enrichment (adding properties to log events):
1. Use a custom sink to capture `LogEvent` instances
2. Assert on `LogEvent.Properties` collection
3. Verify property keys and values match expected enrichment

Example:
```csharp
.WriteTo.Sink(new DelegateSink(logEvent => {
    Assert.Contains("TaskId", logEvent.Properties.Keys);
    Assert.Equal("12345", logEvent.Properties["TaskId"].ToString());
}))
```

### Testing Scope Behavior
For structured logging with scopes:
1. Call `BeginScope()` with state (typically `IEnumerable<KeyValuePair<string, object>>`)
2. Log within the scope
3. Verify enriched properties appear in the captured log event
4. Dispose the scope and verify properties no longer appear

## Notes

- This test project uses **xUnit** while the main test project (`EverTask.Tests`) uses **MSTest**
- Serilog integration uses `LogContext.Push()` for scope enrichment, not the Microsoft.Extensions.Logging scope provider
- The `EverTaskSerilogLogger<T>` returns `NoOpDisposable` for scopes that don't contain KeyValuePair properties
- LogLevel mapping: None->Verbose (not suppressed), Critical->Fatal, Trace->Verbose
- Test pattern favors direct logger instantiation over full DI container setup for unit tests
- Service registration tests use full DI to verify integration, while logger tests use direct instantiation
