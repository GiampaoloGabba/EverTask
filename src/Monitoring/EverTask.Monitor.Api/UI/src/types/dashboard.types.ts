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
}

export interface TasksOverTimeDto {
  timestamp: string;
  completed: number;
  failed: number;
  total: number;
}

export interface QueueSummaryDto {
  queueName: string | null;
  pendingCount: number;
  inProgressCount: number;
  completedCount: number;
  failedCount: number;
}

export interface RecentActivityDto {
  taskId: string;
  type: string;
  status: QueuedTaskStatus;
  timestamp: string;
  message: string;
}
