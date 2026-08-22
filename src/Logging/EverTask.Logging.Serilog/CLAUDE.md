# EverTask.Logging.Serilog

Refer to the root CLAUDE.md for project-wide rules.

Gives EverTask a dedicated Serilog pipeline (own sinks, own `EverTaskSerilog` config section) behind
`IEverTaskLogger<T>`, independent of the host's logging. A host that already calls `UseSerilog()` does not
need it: the default `EverTaskLogger<T>` reaches Serilog through `ILoggerFactory` with full structure.

## Critical rules

- `EverTaskSerilogLogger<T>` is a thin wrapper over `SerilogLoggerProvider` (`Serilog.Extensions.Logging`, the
  official bridge). NEVER replace it with a hand-written `Log<TState>` that renders the message and passes the
  string to `Serilog.ILogger.Write`: that makes the rendered text the message template, loses every named
  property, thrashes Serilog's template cache and re-parses `{…}` fragments inside values (the bug fixed in #32).
- One `SerilogLoggerProvider` per logger instance, on purpose: the bridge keeps its scope stack on the
  provider, so `BeginScope` on one `IEverTaskLogger<T>` enriches that logger only. See the comment on the
  field in `EverTaskSerilogLogger.cs` before changing it.
- `EverTaskSerilogLogger<T>(Serilog.ILogger)` is public and constructed directly by consumers and tests; keep
  the constructor, the unsealed class and the `AddSerilog` overloads source-compatible.

## Gotchas

- Scopes are attached per provider, not pushed into the global `Serilog.Context.LogContext`:
  `Enrich.FromLogContext()` is not needed for EverTask scopes, and an EverTask scope never leaks into the
  host's own Serilog loggers.
- `IsEnabled(LogLevel.None)` is `false`; the old adapter mapped `None` to `Verbose`.
- A plain-string state (`ILogger.Log<string>`) reaches the sink as template `{State:l}`; `RenderMessage()`
  is still the text.

## Tests

`test/EverTask.Tests.Logging/Serilog/` — `SerilogLoggerTests.cs` (template, properties, `EventId`,
`SourceContext`, scopes, `IsEnabled`) and `ServiceRegistrationTests.cs` (DI).
