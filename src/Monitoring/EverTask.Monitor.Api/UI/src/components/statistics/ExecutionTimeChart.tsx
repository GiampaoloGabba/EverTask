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
import { ExecutionTimeDto } from '@/types/statistics.types';
import { format } from 'date-fns';

interface ExecutionTimeChartProps {
  data: ExecutionTimeDto[];
}

export function ExecutionTimeChart({ data }: ExecutionTimeChartProps) {
  const chartData = data.map((item) => ({
    time: format(new Date(item.timestamp), 'MMM d HH:mm'),
    avgTime: item.avgExecutionTimeMs,
  }));

  if (chartData.length === 0) {
    return (
      <Card>
        <CardHeader>
          <CardTitle>Average Execution Time</CardTitle>
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
        <CardTitle>Average Execution Time</CardTitle>
      </CardHeader>
      <CardContent>
        <ResponsiveContainer width="100%" height={300}>
          <LineChart data={chartData}>
            <CartesianGrid strokeDasharray="3 3" />
            <XAxis dataKey="time" />
            <YAxis tickFormatter={(value) => `${value} ms`} />
            <Tooltip formatter={(value: number) => `${value.toFixed(2)} ms`} />
            <Legend />
            <Line
              type="monotone"
              dataKey="avgTime"
              stroke="#3b82f6"
              name="Avg Execution Time"
              strokeWidth={2}
              dot={{ r: 4 }}
            />
          </LineChart>
        </ResponsiveContainer>
      </CardContent>
    </Card>
  );
}
