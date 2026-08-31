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
  version bump of the monitoring contract — which is how 4.0 added the schedule context every event now
  carries: `ParentTaskId` (the schedule an occurrence belongs to), `ScheduledAtUtc` (the nominal slot the
  reported delivery stands for) and `ScheduleVersion` (set on a schedule row and on every occurrence of one,
  null for a task that belongs to no schedule). Both `FromExecutor` overloads fill them — the worker's hot
  path takes the one with the cached type strings — because a second mapping is one that drifts from the one
  production actually publishes.
- **This wire writes nulls; the REST one omits them.** `JsonHubProtocol` has no `WhenWritingNull`, so every
  nullable key is PRESENT holding `null` — a consumer tells a schedule delivery apart by the VALUE of
  `parentTaskId`/`scheduledAtUtc`/`scheduleVersion`, never by the key being there (the API's
  `MonitoringJsonResultFilter` follows the opposite rule, so the same field is an absent key over HTTP).
  `EverTask.Monitor.Api/UI/src/types/signalr.types.ts` mirrors this record and no DTO, which puts it outside
  the DTO walk of `API/Controllers/JsonContractTests`: the payload and that file are held together by
  `SignalR/EventWireContractTests` instead, which reads the hub's JSON as text.
- Event `Message` texts are a parsed contract (`docs/monitoring-events.md`) — consumers match on
  `Rate limit deferred task <id>: key=… slotUtc=<O> policy=… deferredCount=…`, `completed`, `cancelled`,
  `Error occurred` — and are fragments with **no trailing period** since #32. Do not reword them.
- `SignalRMonitoringOptions.IncludeExecutionLogs` defaults to false and the monitor strips `ExecutionLogs`
  from every event unless it is on, which is why the payload's `executionLogs` key is usually there holding
  null — the list is what gets stripped, never the key.
- Server-to-client only: `TaskMonitorHub` exposes no methods, and there is no grouping or filtering.
- No backplane — each host broadcasts only its own events; add Azure SignalR or Redis on the host's
  `AddSignalR()` for multi-instance deployments (`docs/scalability.md`).
- Tests: `test/EverTask.Tests.Monitoring/SignalR/` — monitor, event filtering, execution-log propagation,
  multi-client, reconnection, hub, plus `EventWireContractTests` (4): the raw-JSON guardian of the record
  contract — every key the hub sends is declared by `signalr.types.ts`, an occurrence carries its schedule
  context, and a task belonging to no schedule still gets those keys, holding null.
