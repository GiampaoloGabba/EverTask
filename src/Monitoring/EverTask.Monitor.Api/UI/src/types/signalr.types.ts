// Must match EverTaskEventData exactly (camelCase over the SignalR hub).
//
// This is not the REST wire. The other files here mirror the DTOs of EverTask.Monitor.Api; this one mirrors
// the monitoring record EverTask itself publishes, which SignalRTaskMonitor sends verbatim. Unlike the API,
// the hub does NOT omit nulls — SignalR's JsonHubProtocol has no WhenWritingNull — so a nullable field
// arrives as a key holding null rather than as no key at all. It is still declared OPTIONAL, which is the
// one rule the dashboard's types follow and is true of both shapes. What the hub really carries is pinned by
// test/EverTask.Tests.Monitoring/SignalR/EventWireContractTests.cs.

/** One captured handler log, as EverTask.Storage.TaskExecutionLog reaches the browser. */
export interface TaskExecutionLogData {
  id: string;
  taskId: string;
  timestampUtc: string;
  level: string; // "Trace" | "Debug" | "Information" | "Warning" | "Error" | "Critical"
  message: string;
  exceptionDetails?: string | null;
  sequenceNumber: number;
  // The storage navigation back to the task. The capture never fills it, so it is always null on the wire;
  // declared because the hub writes the key, never to be read.
  task?: null;
}

export interface EverTaskEventData {
  taskId: string;
  eventDateUtc: string;
  severity: 'Information' | 'Warning' | 'Error';
  taskType: string;
  taskHandlerType: string;
  taskParameters: string; // JSON
  message: string;
  exception?: string | null;
  // The logs the handler captured during this delivery. Filled only when persistent logging is on and the
  // hub forwards them (SignalRMonitoringOptions.IncludeExecutionLogs); the key still arrives, holding null.
  executionLogs?: TaskExecutionLogData[] | null;
  // The durable schedule this event's task is an occurrence of. An event about a SCHEDULE — a halt, a
  // reschedule, a provider that could not answer — is published on the schedule row itself, so there `taskId`
  // is the schedule id and this key is null, as it is on any task that belongs to no schedule.
  parentTaskId?: string | null;
  // The nominal slot the reported delivery stands for, when the task is scheduled.
  scheduledAtUtc?: string | null;
  // Version of the schedule definition behind this delivery: written on a schedule row and on every
  // occurrence of one (0 until something reschedules it), null on a task that belongs to no schedule. The
  // three schedule keys are null together, so it is the VALUE that discriminates here and not the presence.
  scheduleVersion?: number | null;
}
