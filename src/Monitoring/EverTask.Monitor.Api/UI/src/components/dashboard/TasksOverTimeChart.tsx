import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import {
  LineChart,
  Line,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  Legend,
  ResponsiveContainer,
} from 'recharts';
import { TasksOverTimeDto } from '@/types/dashboard.types';
import { format } from 'date-fns';

interface TasksOverTimeChartProps {
  data: TasksOverTimeDto[];
}

export function TasksOverTimeChart({ data }: TasksOverTimeChartProps) {
  const chartData = data.map((item) => ({
    ...item,
    time: format(new Date(item.timestamp), 'HH:mm'),
  }));

  if (chartData.length === 0) {
    return (
      <Card>
        <CardHeader>
          <CardTitle>Tasks Over Time</CardTitle>
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
        <CardTitle>Tasks Over Time</CardTitle>
      </CardHeader>
      <CardContent>
        <ResponsiveContainer width="100%" height={300}>
          <LineChart data={chartData}>
            <CartesianGrid strokeDasharray="3 3" />
            <XAxis dataKey="time" />
            <YAxis />
            <Tooltip />
            <Legend />
            <Line
              type="monotone"
              dataKey="total"
              stroke="#3b82f6"
              name="Total"
              strokeWidth={2}
            />
            <Line
              type="monotone"
              dataKey="completed"
              stroke="#22c55e"
              name="Completed"
              strokeWidth={2}
            />
            <Line
              type="monotone"
              dataKey="failed"
              stroke="#ef4444"
              name="Failed"
              strokeWidth={2}
            />
          </LineChart>
        </ResponsiveContainer>
      </CardContent>
    </Card>
  );
}
