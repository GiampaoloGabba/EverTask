import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { PieChart, Pie, Cell, ResponsiveContainer, Legend, Tooltip } from 'recharts';
import { QueuedTaskStatus } from '@/types/task.types';
import { getStatusLabel } from '@/utils/statusHelpers';

interface StatusDistributionChartProps {
  data: Record<QueuedTaskStatus, number>;
}

const COLORS: Record<QueuedTaskStatus, string> = {
  [QueuedTaskStatus.WaitingQueue]: '#3b82f6',
  [QueuedTaskStatus.Queued]: '#3b82f6',
  [QueuedTaskStatus.InProgress]: '#eab308',
  [QueuedTaskStatus.Pending]: '#a855f7',
  [QueuedTaskStatus.Completed]: '#22c55e',
  [QueuedTaskStatus.Failed]: '#ef4444',
  [QueuedTaskStatus.Cancelled]: '#6b7280',
  [QueuedTaskStatus.ServiceStopped]: '#6b7280',
};

export function StatusDistributionChart({ data }: StatusDistributionChartProps) {
  const chartData = Object.entries(data)
    .filter(([, value]) => value > 0)
    .map(([key, value]) => ({
      name: getStatusLabel(Number(key) as QueuedTaskStatus),
      value,
      status: Number(key) as QueuedTaskStatus,
    }));

  if (chartData.length === 0) {
    return (
      <Card>
        <CardHeader>
          <CardTitle>Status Distribution</CardTitle>
        </CardHeader>
        <CardContent>
          <div className="h-[300px] flex items-center justify-center text-muted-foreground">
            No data available
          </div>
        </CardContent>
      </Card>
    );
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Status Distribution</CardTitle>
      </CardHeader>
      <CardContent>
        <ResponsiveContainer width="100%" height={300}>
          <PieChart>
            <Pie
              data={chartData}
              cx="50%"
              cy="50%"
              labelLine={false}
              label={({ name, percent }) => `${name}: ${((percent ?? 0) * 100).toFixed(0)}%`}
              outerRadius={80}
              fill="#8884d8"
              dataKey="value"
            >
              {chartData.map((entry) => (
                <Cell key={`cell-${entry.status}`} fill={COLORS[entry.status]} />
              ))}
            </Pie>
            <Tooltip />
            <Legend />
          </PieChart>
        </ResponsiveContainer>
      </CardContent>
    </Card>
  );
}
