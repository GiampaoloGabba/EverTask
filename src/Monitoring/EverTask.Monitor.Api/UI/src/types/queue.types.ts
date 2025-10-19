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
