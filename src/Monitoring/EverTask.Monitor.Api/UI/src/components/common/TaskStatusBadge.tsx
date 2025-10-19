import { Badge } from '@/components/ui/badge';
import { QueuedTaskStatus } from '@/types/task.types';
import { getStatusLabel, getStatusColor } from '@/utils/statusHelpers';
import { cn } from '@/lib/utils';

interface TaskStatusBadgeProps {
  status: QueuedTaskStatus;
  className?: string;
}

export function TaskStatusBadge({ status, className }: TaskStatusBadgeProps) {
  const label = getStatusLabel(status);
  const colorClass = getStatusColor(status);

  return (
    <Badge
      variant="outline"
      className={cn(
        colorClass,
        'text-white border-0 font-medium',
        className
      )}
    >
      {label}
    </Badge>
  );
}
