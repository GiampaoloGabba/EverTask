export interface QueueMetricsDto {
  queueName: string | null;
  totalTasks: number;
  pendingTasks: number;
  inProgressTasks: number;
  completedTasks: number;
  failedTasks: number;
  avgExecutionTimeMs: number;
  successRate: number;
}

export interface QueueConfigurationDto {
  queueName: string;
  maxDegreeOfParallelism: number;
  channelCapacity: number;
  queueFullBehavior: string;
  defaultTimeout: string | null;
  totalTasks: number;
  pendingTasks: number;
  inProgressTasks: number;
  completedTasks: number;
  failedTasks: number;
  avgExecutionTimeMs: number;
  successRate: number;
  // Rate-limited tasks parked for this queue (in-memory, single-node view). 0 when unused.
  throttledCount: number;
}
