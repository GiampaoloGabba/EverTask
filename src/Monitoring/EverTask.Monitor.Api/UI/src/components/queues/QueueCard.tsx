import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { QueueConfigurationDto } from '@/types/queue.types';
import { formatNumber, formatPercentage, formatTime } from '@/utils/formatters';
import { useNavigate } from 'react-router-dom';
import { Layers, TrendingUp, Clock, Settings, Users, Database } from 'lucide-react';

interface QueueCardProps {
  queue: QueueConfigurationDto;
}

export function QueueCard({ queue }: QueueCardProps) {
  const navigate = useNavigate();

  const getSuccessRateColor = (rate: number) => {
    if (rate >= 95) return 'text-green-600 bg-green-50 border-green-200';
    if (rate >= 80) return 'text-yellow-600 bg-yellow-50 border-yellow-200';
    return 'text-red-600 bg-red-50 border-red-200';
  };

  const handleClick = () => {
    // Navigate to tasks page with queue filter
    navigate(`/tasks?queue=${encodeURIComponent(queue.queueName || 'default')}`);
  };

  return (
    <Card
      className="cursor-pointer hover:shadow-md transition-shadow"
      onClick={handleClick}
    >
      <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
        <CardTitle className="text-lg font-semibold flex items-center gap-2">
          <Layers className="h-5 w-5 text-blue-600" />
          {queue.queueName || 'Default Queue'}
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-4">
        {/* Configuration Section */}
        <div className="rounded-lg bg-muted/50 p-3 space-y-2">
          <div className="flex items-center gap-1 text-xs font-semibold text-muted-foreground mb-2">
            <Settings className="h-3 w-3" />
            <span>Configuration</span>
          </div>
          <div className="grid grid-cols-2 gap-2 text-xs">
            <div className="flex items-center gap-1">
              <Users className="h-3 w-3 text-muted-foreground" />
              <span className="text-muted-foreground">Workers:</span>
              <span className="font-medium">{queue.maxDegreeOfParallelism}</span>
            </div>
            <div className="flex items-center gap-1">
              <Database className="h-3 w-3 text-muted-foreground" />
              <span className="text-muted-foreground">Capacity:</span>
              <span className="font-medium">{formatNumber(queue.channelCapacity)}</span>
            </div>
            <div className="col-span-2 flex items-center gap-1">
              <span className="text-muted-foreground">Behavior:</span>
              <Badge variant="outline" className="text-xs">
                {queue.queueFullBehavior}
              </Badge>
            </div>
            {queue.defaultTimeout && (
              <div className="col-span-2 flex items-center gap-1">
                <Clock className="h-3 w-3 text-muted-foreground" />
                <span className="text-muted-foreground">Timeout:</span>
                <span className="font-medium">{queue.defaultTimeout}</span>
              </div>
            )}
          </div>
        </div>

        {/* Total Tasks */}
        <div className="flex items-center justify-between">
          <span className="text-sm text-muted-foreground">Total Tasks</span>
          <span className="text-2xl font-bold">{formatNumber(queue.totalTasks)}</span>
        </div>

        {/* Status Breakdown */}
        <div className="grid grid-cols-2 gap-3">
          <div className="space-y-1">
            <div className="flex items-center justify-between">
              <span className="text-xs text-muted-foreground">Pending</span>
              <Badge variant="outline" className="bg-blue-50 text-blue-700 border-blue-200">
                {formatNumber(queue.pendingTasks)}
              </Badge>
            </div>
            <div className="flex items-center justify-between">
              <span className="text-xs text-muted-foreground">In Progress</span>
              <Badge variant="outline" className="bg-yellow-50 text-yellow-700 border-yellow-200">
                {formatNumber(queue.inProgressTasks)}
              </Badge>
            </div>
          </div>
          <div className="space-y-1">
            <div className="flex items-center justify-between">
              <span className="text-xs text-muted-foreground">Completed</span>
              <Badge variant="outline" className="bg-green-50 text-green-700 border-green-200">
                {formatNumber(queue.completedTasks)}
              </Badge>
            </div>
            <div className="flex items-center justify-between">
              <span className="text-xs text-muted-foreground">Failed</span>
              <Badge variant="outline" className="bg-red-50 text-red-700 border-red-200">
                {formatNumber(queue.failedTasks)}
              </Badge>
            </div>
          </div>
        </div>

        {/* Metrics */}
        <div className="pt-3 border-t space-y-2">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-1 text-sm text-muted-foreground">
              <TrendingUp className="h-3 w-3" />
              <span>Success Rate</span>
            </div>
            <Badge variant="outline" className={getSuccessRateColor(queue.successRate)}>
              {formatPercentage(queue.successRate)}
            </Badge>
          </div>
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-1 text-sm text-muted-foreground">
              <Clock className="h-3 w-3" />
              <span>Avg Time</span>
            </div>
            <span className="text-sm font-medium">{formatTime(queue.avgExecutionTimeMs)}</span>
          </div>
        </div>
      </CardContent>
    </Card>
  );
}
