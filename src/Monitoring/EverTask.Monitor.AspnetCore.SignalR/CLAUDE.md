# EverTask.Monitor.AspnetCore.SignalR

Refer to the root CLAUDE.md for project-wide rules.

`SignalRTaskMonitor` subscribes to `IEverTaskWorkerExecutor.TaskEventOccurredAsync` and pushes every
`EverTaskEventData` to `Clients.All` under the `EverTaskEvent` method name.

## Wiring

- `AddSignalRMonitoring()` only registers the monitor (`TryAddSingleton<ITaskMonitor, SignalRTaskMonitor>`
  plus `AddSignalR()`): **`MapEverTaskMonitorHub()` is what calls `SubScribe()`**, and it must run after the
  app is built, before `app.Run()`. Omit it and the monitor exists but no event ever fires; the
  `MonitorNotRegistered` warning covers only the opposite case (monitor missing).
- Default hub pattern `/evertask-monitoring/hub`. Probe it with `POST {hub}/negotiate?negotiateVersion=1`,
  not `HEAD {hub}`; behind `EverTask.Monitor.Api` with the default `EnableAuthentication` an
  unauthenticated probe answers 401.

## Invariants

- **`EverTaskEventData` (`src/EverTask/Monitoring/EverTaskEventData.cs`) is a positional public record**
  consumed by external `ITaskMonitor` implementations: adding or reordering positional parameters is
  binary-breaking. New data goes in new event types or non-positional `init` properties, bundled with a
  monitoring-contract minor.
- Event `Message` texts are a parsed contract (`docs/monitoring-events.md`) — consumers match on
  `Rate limit deferred task <id>: key=… slotUtc=<O> policy=… deferredCount=…`, `completed`, `cancelled`,
  `Error occurred` — and are fragments with **no trailing period** since #32. Do not reword them.
- `SignalRMonitoringOptions.IncludeExecutionLogs` defaults to false and the monitor strips `ExecutionLogs`
  from every event unless it is on, which is why the payload's `ExecutionLogs` is usually null.
- Server-to-client only: `TaskMonitorHub` exposes no methods, and there is no grouping or filtering.
- No backplane — each host broadcasts only its own events; add Azure SignalR or Redis on the host's
  `AddSignalR()` for multi-instance deployments (`docs/scalability.md`).
- Tests: `test/EverTask.Tests.Monitoring/SignalR/` — monitor, event filtering, execution-log propagation,
  multi-client, reconnection, hub.
