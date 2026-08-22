# EverTask.Tests.Logging

Refer to the root CLAUDE.md for project-wide rules. Tests for the logging integrations (Serilog today); xUnit +
Shouldly, multi-target net8.0/net9.0/net10.0, so every assertion must hold on Serilog.Extensions.Logging
8.0.0 / 9.0.0 / 10.0.0.

## Critical rules

- Capture events into a list (`SerilogLoggerTests.CreateLogger` + `DelegateSink`) and assert AFTER the log
  call. NEVER assert inside the sink delegate: it only runs when an event is emitted, so a test passes
  vacuously when the adapter logs nothing — that is how the property loss fixed in #32 went unnoticed.
- Assert on `MessageTemplate.Text` and `Properties[...]`, not only on `RenderMessage()`: the rendered text
  looks right even when the structure is lost.

## Gotchas

- `LogEvent.Properties` is an `IReadOnlyDictionary`: Shouldly's `ShouldNotContainKey` does not bind, use
  `Properties.ContainsKey("X").ShouldBeFalse()`.
- `Properties["EventId"]` is a `StructureValue` with `Id` / `Name` sub-properties, not a scalar.
- `TestLog` (source-generated `[LoggerMessage]` class used to cover the generator's state struct) needs the
  DIRECT `Microsoft.Extensions.Logging.Abstractions` PackageReference in this csproj: analyzer assets do not
  flow through the project reference.

## Adding a logger integration

Create `test/EverTask.Tests.Logging/<Provider>/` with `ServiceRegistrationTests.cs` (DI resolves
`IEverTaskLogger<T>` to the provider type) and `<Provider>LoggerTests.cs` covering the same matrix as the
Serilog one.
