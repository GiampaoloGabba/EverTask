import { QueuedTaskStatus } from './task.types';

export enum DateRange {
  Today = 'Today',
  Week = 'Week',
  Month = 'Month',
  All = 'All'
}

export interface OverviewDto {
  totalTasksToday: number;
  totalTasksWeek: number;
  successRate: number;
  failedCount: number;
  avgExecutionTimeMs: number;
  statusDistribution: Record<QueuedTaskStatus, number>;
  tasksOverTime: TasksOverTimeDto[];
  queueSummaries: QueueSummaryDto[];
  // Rate-limited tasks currently parked waiting for budget (in-memory, single-node view).
  throttledTasks: number;
  // Materialized occurrences by state, and how far behind the oldest pending one is.
  catchUpBacklog: CatchUpBacklogDto;
}

export interface CatchUpBacklogDto {
  pending: number;
  active: number;
  failed: number;
  // Occurrences that will never run: cancelled on their own or with their schedule.
  skipped: number;
  completed: number;
  oldestPendingSlotUtc?: string | null;
  lagSeconds: number;
  // Durable schedules whose catch-up halted itself over the overflow cap. A halt never releases itself.
  haltedSchedules: number;
}

export interface TasksOverTimeDto {
  timestamp: string;
  completed: number;
  failed: number;
  total: number;
}

export interface QueueSummaryDto {
  queueName?: string | null;
  pendingCount: number;
  inProgressCount: number;
  completedCount: number;
  failedCount: number;
  // Rate-limited tasks parked for this queue (in-memory, single-node view).
  throttledCount: number;
}

export interface RecentActivityDto {
  taskId: string;
  type: string;
  status: QueuedTaskStatus;
  timestamp: string;
  message: string;
}
