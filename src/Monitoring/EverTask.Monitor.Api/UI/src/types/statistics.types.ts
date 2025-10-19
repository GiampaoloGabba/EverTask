export enum TimePeriod {
  Last7Days = 'Last7Days',
  Last30Days = 'Last30Days',
  Last90Days = 'Last90Days'
}

export interface SuccessRateTrendDto {
  timestamps: string[];
  successRates: number[];
}

export interface ExecutionTimeDto {
  timestamp: string;
  avgExecutionTimeMs: number;
}
