import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { PieChart, Pie, Cell, ResponsiveContainer, Legend, Tooltip } from 'recharts';

interface TaskTypeDistributionChartProps {
  data: Record<string, number>;
}

const COLORS = [
  '#3b82f6', // blue
  '#22c55e', // green
  '#eab308', // yellow
  '#ef4444', // red
  '#a855f7', // purple
  '#06b6d4', // cyan
  '#f97316', // orange
  '#ec4899', // pink
  '#8b5cf6', // violet
  '#14b8a6', // teal
];

export function TaskTypeDistributionChart({ data }: TaskTypeDistributionChartProps) {
  const chartData = Object.entries(data).map(([key, value], index) => {
    // Extract short name from type
    const parts = key.split('.');
    const shortName = parts[parts.length - 1] || key;

    return {
      name: shortName,
      fullName: key,
      value,
      color: COLORS[index % COLORS.length],
    };
  });

  if (chartData.length === 0) {
    return (
      <Card>
        <CardHeader>
          <CardTitle>Task Type Distribution</CardTitle>
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
        <CardTitle>Task Type Distribution</CardTitle>
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
              {chartData.map((entry, index) => (
                <Cell key={`cell-${index}`} fill={entry.color} />
              ))}
            </Pie>
            <Tooltip
              formatter={(value, _name, props) => [
                value as number,
                (props as any).payload.fullName,
              ]}
            />
            <Legend />
          </PieChart>
        </ResponsiveContainer>
      </CardContent>
    </Card>
  );
}
