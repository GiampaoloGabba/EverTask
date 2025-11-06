import { useState } from 'react';
import { useSuccessRateTrend, useTaskTypeDistribution, useExecutionTimes } from '@/hooks/useStatistics';
import { useDashboardOverview } from '@/hooks/useDashboard';
import { SuccessRateTrendChart } from '@/components/statistics/SuccessRateTrendChart';
import { TaskTypeDistributionChart } from '@/components/statistics/TaskTypeDistributionChart';
import { ExecutionTimeChart } from '@/components/statistics/ExecutionTimeChart';
import { StatusDistributionChart } from '@/components/dashboard/StatusDistributionChart';
import { TasksOverTimeChart } from '@/components/dashboard/TasksOverTimeChart';
import { LoadingSpinner } from '@/components/common/LoadingSpinner';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { TimePeriod } from '@/types/statistics.types';
import { DateRange } from '@/types/dashboard.types';
import { AlertCircle } from 'lucide-react';

export function StatisticsPage() {
  const [overviewRange, setOverviewRange] = useState<DateRange>(DateRange.Today);
  const [trendPeriod, setTrendPeriod] = useState<TimePeriod>(TimePeriod.Last7Days);
  const [distributionRange, setDistributionRange] = useState<DateRange>(DateRange.Week);
  const [executionRange, setExecutionRange] = useState<DateRange>(DateRange.Today);

  const {
    data: overview,
    isLoading: isLoadingOverview,
    isError: isErrorOverview,
  } = useDashboardOverview(overviewRange);

  const {
    data: successRateTrend,
    isLoading: isLoadingTrend,
    isError: isErrorTrend,
  } = useSuccessRateTrend(trendPeriod);

  const {
    data: taskTypeDistribution,
    isLoading: isLoadingDistribution,
    isError: isErrorDistribution,
  } = useTaskTypeDistribution(distributionRange);

  const {
    data: executionTimes,
    isLoading: isLoadingExecution,
    isError: isErrorExecution,
  } = useExecutionTimes(executionRange);

  const isLoading = isLoadingOverview || isLoadingTrend || isLoadingDistribution || isLoadingExecution;
  const isError = isErrorOverview || isErrorTrend || isErrorDistribution || isErrorExecution;

  if (isLoading) {
    return <LoadingSpinner text="Loading statistics..." />;
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-3xl font-bold tracking-tight">Statistics</h1>
        <p className="text-muted-foreground">
          Analyze task performance and trends over time
        </p>
      </div>

      {isError && (
        <Alert variant="destructive">
          <AlertCircle className="h-4 w-4" />
          <AlertDescription>
            Failed to load some statistics. Please try refreshing the page.
          </AlertDescription>
        </Alert>
      )}

      {/* Period Selectors */}
      <div className="flex flex-wrap gap-4">
        <div className="flex items-center gap-2">
          <span className="text-sm text-muted-foreground">Overview Period:</span>
          <Select
            value={overviewRange}
            onValueChange={(value) => setOverviewRange(value as DateRange)}
          >
            <SelectTrigger className="w-[180px]">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={DateRange.Today}>Today</SelectItem>
              <SelectItem value={DateRange.Week}>This Week</SelectItem>
              <SelectItem value={DateRange.Month}>This Month</SelectItem>
              <SelectItem value={DateRange.All}>All Time</SelectItem>
            </SelectContent>
          </Select>
        </div>

        <div className="flex items-center gap-2">
          <span className="text-sm text-muted-foreground">Success Rate Period:</span>
          <Select value={trendPeriod} onValueChange={(value) => setTrendPeriod(value as TimePeriod)}>
            <SelectTrigger className="w-[180px]">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={TimePeriod.Last7Days}>Last 7 Days</SelectItem>
              <SelectItem value={TimePeriod.Last30Days}>Last 30 Days</SelectItem>
              <SelectItem value={TimePeriod.Last90Days}>Last 90 Days</SelectItem>
            </SelectContent>
          </Select>
        </div>

        <div className="flex items-center gap-2">
          <span className="text-sm text-muted-foreground">Distribution Period:</span>
          <Select
            value={distributionRange}
            onValueChange={(value) => setDistributionRange(value as DateRange)}
          >
            <SelectTrigger className="w-[180px]">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={DateRange.Today}>Today</SelectItem>
              <SelectItem value={DateRange.Week}>This Week</SelectItem>
              <SelectItem value={DateRange.Month}>This Month</SelectItem>
            </SelectContent>
          </Select>
        </div>

        <div className="flex items-center gap-2">
          <span className="text-sm text-muted-foreground">Execution Time Period:</span>
          <Select
            value={executionRange}
            onValueChange={(value) => setExecutionRange(value as DateRange)}
          >
            <SelectTrigger className="w-[180px]">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={DateRange.Today}>Today</SelectItem>
              <SelectItem value={DateRange.Week}>This Week</SelectItem>
              <SelectItem value={DateRange.Month}>This Month</SelectItem>
            </SelectContent>
          </Select>
        </div>
      </div>

      {/* Status Distribution & Tasks Over Time Charts */}
      {overview && (
        <div className="grid gap-6 md:grid-cols-2">
          <StatusDistributionChart data={overview.statusDistribution} />
          <TasksOverTimeChart data={overview.tasksOverTime} />
        </div>
      )}

      {/* Success Rate & Task Type Distribution Charts */}
      <div className="grid gap-6 md:grid-cols-2">
        {successRateTrend && <SuccessRateTrendChart data={successRateTrend} />}
        {taskTypeDistribution && <TaskTypeDistributionChart data={taskTypeDistribution} />}
      </div>

      {/* Execution Time Chart */}
      <div className="grid gap-6">
        {executionTimes && <ExecutionTimeChart data={executionTimes} />}
      </div>
    </div>
  );
}
