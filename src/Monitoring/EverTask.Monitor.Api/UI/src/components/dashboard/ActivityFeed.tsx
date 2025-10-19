import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { ScrollArea } from '@/components/ui/scroll-area';
import { TaskStatusBadge } from '@/components/common/TaskStatusBadge';
import { RecentActivityDto } from '@/types/dashboard.types';
import { formatDistanceToNow } from 'date-fns';
import { useRecentActivity } from '@/hooks/useDashboard';
import { LoadingSpinner } from '@/components/common/LoadingSpinner';
import { AlertCircle } from 'lucide-react';

export function ActivityFeed() {
  const { data: activities, isLoading, isError } = useRecentActivity(20);

  return (
    <Card>
      <CardHeader>
        <CardTitle>Recent Activity</CardTitle>
      </CardHeader>
      <CardContent>
        {isLoading && <LoadingSpinner text="Loading activity..." size="sm" />}

        {isError && (
          <div className="flex items-center gap-2 text-sm text-red-600 py-4">
            <AlertCircle className="h-4 w-4" />
            <span>Failed to load activity</span>
          </div>
        )}

        {!isLoading && !isError && activities && activities.length === 0 && (
          <div className="text-center py-8 text-muted-foreground text-sm">
            No recent activity
          </div>
        )}

        {!isLoading && !isError && activities && activities.length > 0 && (
          <ScrollArea className="h-[400px] pr-4">
            <div className="space-y-4">
              {activities.map((activity: RecentActivityDto) => (
                <div key={activity.taskId} className="flex gap-3 pb-4 border-b last:border-0">
                  <div className="flex-1 space-y-1">
                    <div className="flex items-center gap-2 flex-wrap">
                      <TaskStatusBadge status={activity.status} />
                      <span className="text-xs text-muted-foreground">
                        {formatDistanceToNow(new Date(activity.timestamp), { addSuffix: true })}
                      </span>
                    </div>
                    <p className="text-sm font-medium text-gray-900 truncate">
                      {activity.type}
                    </p>
                    {activity.message && (
                      <p className="text-xs text-muted-foreground">{activity.message}</p>
                    )}
                  </div>
                </div>
              ))}
            </div>
          </ScrollArea>
        )}
      </CardContent>
    </Card>
  );
}
