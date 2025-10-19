// Must match backend DTOs exactly (camelCase for JSON serialization)

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

export interface TaskListDto {
  id: string;
  type: string;
  status: QueuedTaskStatus;
  queueName: string | null;
  createdAtUtc: string;
  lastExecutionUtc: string | null;
  scheduledExecutionUtc: string | null;
  isRecurring: boolean;
  recurringInfo: string | null;
  currentRunCount: number | null;
  maxRuns: number | null;
}

export interface TaskDetailDto extends TaskListDto {
  handler: string;
  request: string; // JSON string
  taskKey: string | null;
  exception: string | null;
  recurringTask: string | null; // JSON string
  runUntil: string | null;
  nextRunUtc: string | null;
  statusAudits: StatusAuditDto[];
  runsAudits: RunsAuditDto[];
}

export interface StatusAuditDto {
  id: number;
  queuedTaskId: string;
  updatedAtUtc: string;
  newStatus: QueuedTaskStatus;
  exception: string | null;
}

export interface RunsAuditDto {
  id: number;
  queuedTaskId: string;
  executedAt: string;
  status: QueuedTaskStatus;
  exception: string | null;
}

export interface TaskFilter {
  statuses?: QueuedTaskStatus[];
  taskType?: string;
  queueName?: string;
  isRecurring?: boolean;
  createdAfter?: string;
  createdBefore?: string;
  searchTerm?: string;
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
