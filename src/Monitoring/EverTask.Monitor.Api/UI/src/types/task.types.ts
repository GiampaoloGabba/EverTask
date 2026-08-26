// Must match backend DTOs exactly (camelCase for JSON serialization)
// Backend uses JsonStringEnumConverter for JSON but numbers for query params
//
// Every nullable field is declared OPTIONAL because the API omits nulls rather than writing them
// (MonitoringJsonResultFilter sets JsonIgnoreCondition.WhenWritingNull): a one-shot's response carries no
// `parentTaskId` key at all, so a required `parentTaskId: string | null` would be a promise the wire never
// keeps and nothing restores it on the way in. Which keys are absent is pinned server-side by
// test/EverTask.Tests.Monitoring/API/Controllers/JsonContractTests.cs.

export enum QueuedTaskStatus {
  WaitingQueue = 0,
  Queued = 1,
  InProgress = 2,
  Pending = 3,
  Cancelled = 4,
  Completed = 5,
  Failed = 6,
  ServiceStopped = 7
}

// The API serializes these as strings (JsonStringEnumConverter), unlike QueuedTaskStatus,
// which the response interceptor converts back to its numeric form.
export type OccurrenceMode = 'Inline' | 'Durable';
export type MisfirePolicy = 'Skip' | 'FireOnce' | 'CatchUp';
export type MisfireKind = 'None' | 'Late' | 'FireOnce' | 'CatchUp';

export enum AuditLevel {
  Full = 0,
  Minimal = 1,
  ErrorsOnly = 2,
  None = 3
}

// String to enum mapping (for JSON deserialization from API)
export const QueuedTaskStatusFromString: Record<string, QueuedTaskStatus> = {
  'WaitingQueue': QueuedTaskStatus.WaitingQueue,
  'Queued': QueuedTaskStatus.Queued,
  'InProgress': QueuedTaskStatus.InProgress,
  'Pending': QueuedTaskStatus.Pending,
  'Cancelled': QueuedTaskStatus.Cancelled,
  'Completed': QueuedTaskStatus.Completed,
  'Failed': QueuedTaskStatus.Failed,
  'ServiceStopped': QueuedTaskStatus.ServiceStopped,
};

export interface TaskListDto {
  id: string;
  type: string;
  status: QueuedTaskStatus;
  queueName?: string | null;
  taskKey?: string | null;
  createdAtUtc: string;
  lastExecutionUtc?: string | null;
  scheduledExecutionUtc?: string | null;
  isRecurring: boolean;
  recurringInfo?: string | null;
  currentRunCount?: number | null;
  maxRuns?: number | null;
  executionTimeMs: number; // Last execution time in milliseconds
  // Reserved rate-limit slot (UTC) when the task is currently parked by the limiter, null otherwise.
  // In-memory single-node overlay: only this process' parked tasks are visible.
  throttledUntil?: string | null;
  // The durable schedule this task is an occurrence of, or null for anything else.
  parentTaskId?: string | null;
  // How the schedule behind this row produces its occurrences; null for a task that belongs to none.
  occurrenceMode?: OccurrenceMode | null;
  // What the schedule does with a slot that came due with nothing to run it. Only a schedule row states it.
  misfirePolicy?: MisfirePolicy | null;
  // The IANA zone the schedule's calendar is read on, or null for plain UTC.
  timeZoneId?: string | null;
  // Version of the schedule definition this row belongs to (a schedule row or an occurrence of one, where a
  // row never rescheduled reads 0); null for a task that belongs to no schedule.
  scheduleVersion?: number | null;
  // The nominal slot an occurrence stands for, or null on anything that is not one.
  nominalSlotUtc?: string | null;
  // When the last (or current) run began, or null while nothing has run it. This — never lastExecutionUtc,
  // which is stamped when a run ENDS — is the term lateness is measured against.
  startedAtUtc?: string | null;
  // What kind of missed work an occurrence stands for, or null when it is simply its own slot.
  misfireKind?: MisfireKind | null;
}

export interface OccurrenceInfoDto {
  slotUtc?: string | null;
  runNumber?: number | null;
  timeZoneId?: string | null;
  misfireKind?: MisfireKind | null;
  missedFromUtc?: string | null;
  missedThroughUtc?: string | null;
  missedCount?: number | null;
  missedCountIsExact?: boolean | null;
}

export interface ScheduleHaltDto {
  atUtc: string;
  reason?: string | null;
  detectedAtLeast: number;
  isExact: boolean;
  cursorUtc?: string | null;
  scheduleVersion: number;
}

export interface OccurrenceDto {
  id: string;
  parentTaskId: string;
  status: QueuedTaskStatus;
  occurrence: OccurrenceInfoDto;
  createdAtUtc: string;
  lastExecutionUtc?: string | null;
  executionTimeMs: number;
  exception?: string | null;
  scheduleVersion: number;
  // When this occurrence's run began; see TaskListDto.startedAtUtc.
  startedAtUtc?: string | null;
}

export interface OccurrencesResponse {
  occurrences: OccurrenceDto[];
  totalCount: number;
  skip: number;
  take: number;
}

export interface TaskDetailDto extends TaskListDto {
  handler: string;
  request: string; // JSON string
  taskKey?: string | null;
  exception?: string | null;
  recurringTask?: string | null; // JSON string
  runUntil?: string | null;
  nextRunUtc?: string | null;
  auditLevel?: number | null; // AuditLevel enum value
  // The FIRST page of each trail, not the whole of it. The totals below are what say there is more, and
  // GET /tasks/{id}/status-audit | /runs-audit is where the rest is asked for.
  statusAudits: StatusAuditDto[];
  runsAudits: RunsAuditDto[];
  statusAuditsTotalCount: number;
  runsAuditsTotalCount: number;
  // The occurrence metadata this row carries; null on a schedule row and on an ordinary one-shot.
  occurrence?: OccurrenceInfoDto | null;
  // The standing catch-up halt of a durable schedule; null while it is running.
  halt?: ScheduleHaltDto | null;
}

export interface StatusAuditDto {
  id: number;
  queuedTaskId: string;
  updatedAtUtc: string;
  newStatus: QueuedTaskStatus;
  exception?: string | null;
}

export interface RunsAuditDto {
  id: number;
  queuedTaskId: string;
  executedAt: string;
  executionTimeMs: number; // Execution time in milliseconds
  status: QueuedTaskStatus;
  exception?: string | null;
}

export interface StatusAuditsResponse {
  audits: StatusAuditDto[];
  totalCount: number;
  skip: number;
  take: number;
}

export interface RunsAuditsResponse {
  audits: RunsAuditDto[];
  totalCount: number;
  skip: number;
  take: number;
}

export interface TaskFilter {
  statuses?: QueuedTaskStatus[];
  taskType?: string;
  queueName?: string;
  isRecurring?: boolean;
  createdAfter?: string;
  createdBefore?: string;
  searchTerm?: string;
  parentTaskId?: string;
  onlyOccurrences?: boolean;
  onlyCatchUp?: boolean;
}

export interface PaginationParams {
  page: number;
  pageSize: number;
  sortBy?: string;
  sortDescending?: boolean;
}

export interface TasksPagedResponse {
  items: TaskListDto[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

export interface ExecutionLogDto {
  id: string;
  timestampUtc: string;
  level: string; // "Trace", "Debug", "Information", "Warning", "Error", "Critical"
  message: string;
  exceptionDetails?: string | null;
  sequenceNumber: number;
}

export interface ExecutionLogsResponse {
  logs: ExecutionLogDto[];
  totalCount: number;
  skip: number;
  take: number;
}

export interface TaskCountsDto {
  all: number;
  standard: number;
  recurring: number;
  failed: number;
  // Materialized occurrences; also counted inside `standard`, since each of them is a one-shot row.
  occurrences: number;
}
