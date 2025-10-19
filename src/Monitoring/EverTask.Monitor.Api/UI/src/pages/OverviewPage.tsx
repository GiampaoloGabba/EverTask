import { useDashboardOverview } from '@/hooks/useDashboard';
import { KPICard } from '@/components/dashboard/KPICard';
import { StatusDistributionChart } from '@/components/dashboard/StatusDistributionChart';
import { TasksOverTimeChart } from '@/components/dashboard/TasksOverTimeChart';
import { ActivityFeed } from '@/components/dashboard/ActivityFeed';
import { LoadingSpinner } from '@/components/common/LoadingSpinner';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { DateRange } from '@/types/dashboard.types';
import { CheckCircle2, XCircle, TrendingUp, Clock, AlertCircle } from 'lucide-react';
import { formatNumber, formatPercentage } from '@/utils/formatters';

export function OverviewPage() {
  const { data: overview, isLoading, isError } = useDashboardOverview(DateRange.Today);

  if (isLoading) {
    return <LoadingSpinner text="Loading dashboard..." />;
  }

  if (isError || !overview) {
    return (
      <Alert variant="destructive">
        <AlertCircle className="h-4 w-4" />
        <AlertDescription>
          Failed to load dashboard data. Please try refreshing the page.
        </AlertDescription>
      </Alert>
    );
  }

  const formatTime = (ms: number) => {
    if (ms < 1000) {
      return `${ms.toFixed(0)} ms`;
    }
    return `${(ms / 1000).toFixed(2)} s`;
  };

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-3xl font-bold tracking-tight">Overview</h1>
        <p className="text-muted-foreground">
          Monitor your background tasks and system performance
        </p>
      </div>

      {/* KPI Cards */}
      <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-4">
        <KPICard
          icon={TrendingUp}
          title="Total Tasks Today"
          value={formatNumber(overview.totalTasksToday)}
          subtitle={`${formatNumber(overview.totalTasksWeek)} this week`}
        />
        <KPICard
          icon={CheckCircle2}
          title="Success Rate"
          value={formatPercentage(overview.successRate)}
          subtitle="Overall completion rate"
        />
        <KPICard
          icon={XCircle}
          title="Failed Tasks"
          value={formatNumber(overview.failedCount)}
          subtitle="Requires attention"
        />
        <KPICard
          icon={Clock}
          title="Avg Execution Time"
          value={formatTime(overview.avgExecutionTimeMs)}
          subtitle="Average processing time"
        />
      </div>

      {/* Charts */}
      <div className="grid gap-4 md:grid-cols-2">
        <StatusDistributionChart data={overview.statusDistribution} />
        <TasksOverTimeChart data={overview.tasksOverTime} />
      </div>

      {/* Activity Feed */}
      <ActivityFeed />
    </div>
  );
}
