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
import { SuccessRateTrendDto } from '@/types/statistics.types';
import { format } from 'date-fns';

interface SuccessRateTrendChartProps {
  data: SuccessRateTrendDto;
}

export function SuccessRateTrendChart({ data }: SuccessRateTrendChartProps) {
  const chartData = data.timestamps.map((timestamp, index) => ({
    time: format(new Date(timestamp), 'MMM d'),
    successRate: data.successRates[index],
  }));

  if (chartData.length === 0) {
    return (
      <Card>
        <CardHeader>
          <CardTitle>Success Rate Trend</CardTitle>
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
        <CardTitle>Success Rate Trend</CardTitle>
      </CardHeader>
      <CardContent>
        <ResponsiveContainer width="100%" height={300}>
          <LineChart data={chartData}>
            <CartesianGrid strokeDasharray="3 3" />
            <XAxis dataKey="time" />
            <YAxis domain={[0, 100]} tickFormatter={(value) => `${value}%`} />
            <Tooltip formatter={(value) => `${(value as number).toFixed(1)}%`} />
            <Legend />
            <Line
              type="monotone"
              dataKey="successRate"
              stroke="#22c55e"
              name="Success Rate"
              strokeWidth={2}
              dot={{ r: 4 }}
            />
          </LineChart>
        </ResponsiveContainer>
      </CardContent>
    </Card>
  );
}
