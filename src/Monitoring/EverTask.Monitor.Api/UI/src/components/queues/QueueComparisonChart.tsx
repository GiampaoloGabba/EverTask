import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import {
  BarChart,
  Bar,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  Legend,
  ResponsiveContainer,
} from 'recharts';
import { QueueMetricsDto } from '@/types/queue.types';

interface QueueComparisonChartProps {
  queues: QueueMetricsDto[];
}

export function QueueComparisonChart({ queues }: QueueComparisonChartProps) {
  const chartData = queues.map((queue) => ({
    name: queue.queueName || 'Default',
    Pending: queue.pendingTasks,
    'In Progress': queue.inProgressTasks,
    Completed: queue.completedTasks,
    Failed: queue.failedTasks,
  }));

  if (chartData.length === 0) {
    return (
      <Card>
        <CardHeader>
          <CardTitle>Queue Comparison</CardTitle>
        </CardHeader>
        <CardContent>
          <div className="h-[300px] flex items-center justify-center text-muted-foreground">
            No queue data available
          </div>
        </CardContent>
      </Card>
    );
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Queue Comparison</CardTitle>
      </CardHeader>
      <CardContent>
        <ResponsiveContainer width="100%" height={300}>
          <BarChart data={chartData}>
            <CartesianGrid strokeDasharray="3 3" />
            <XAxis dataKey="name" />
            <YAxis />
            <Tooltip />
            <Legend />
            <Bar dataKey="Pending" fill="#3b82f6" />
            <Bar dataKey="In Progress" fill="#eab308" />
            <Bar dataKey="Completed" fill="#22c55e" />
            <Bar dataKey="Failed" fill="#ef4444" />
          </BarChart>
        </ResponsiveContainer>
      </CardContent>
    </Card>
  );
}
