import { TaskStatusBadge } from './TaskStatusBadge';
import { QueuedTaskStatus } from '@/types/task.types';
import { formatDistanceToNow } from 'date-fns';
import { ChevronDown, ChevronRight } from 'lucide-react';
import { useState } from 'react';
import { cn } from '@/lib/utils';

interface TimelineItem {
  id: number;
  timestamp: string;
  status: QueuedTaskStatus;
  message?: string;
  exception?: string | null;
  executionTimeMs?: number;
}

interface TimelineProps {
  items: TimelineItem[];
  className?: string;
}

function TimelineItemComponent({ item, isLast }: { item: TimelineItem; isLast: boolean }) {
  const [isExpanded, setIsExpanded] = useState(false);
  const hasException = item.exception && item.exception.trim() !== '';

  const formatExecutionTime = (ms: number) => {
    if (ms < 1000) return `${ms.toFixed(0)}ms`;
    return `${(ms / 1000).toFixed(2)}s`;
  };

  return (
    <div className={cn('relative pl-8', !isLast && 'pb-6')}>
      {/* Timeline dot and line */}
      <div className="absolute left-0 top-1.5">
        <div className="w-3 h-3 rounded-full bg-blue-600 ring-4 ring-blue-50" />
      </div>
      {!isLast && (
        <div className="absolute left-1.5 top-5 bottom-0 w-0.5 bg-gray-200" />
      )}

      {/* Content */}
      <div className="space-y-2">
        <div className="flex items-center gap-3 flex-wrap">
          <TaskStatusBadge status={item.status} />
          <span className="text-sm text-muted-foreground">
            {formatDistanceToNow(new Date(item.timestamp), { addSuffix: true })}
          </span>
          {item.executionTimeMs !== undefined && item.executionTimeMs > 0 && (
            <span className="text-sm font-mono text-gray-600">
              {formatExecutionTime(item.executionTimeMs)}
            </span>
          )}
        </div>

        {item.message && (
          <p className="text-sm text-gray-700">{item.message}</p>
        )}

        {hasException && (
          <div className="mt-2">
            <button
              onClick={() => setIsExpanded(!isExpanded)}
              className="flex items-center gap-1 text-sm text-red-600 hover:text-red-700 transition-colors"
            >
              {isExpanded ? (
                <ChevronDown className="h-4 w-4" />
              ) : (
                <ChevronRight className="h-4 w-4" />
              )}
              <span className="font-medium">Exception details</span>
            </button>
            {isExpanded && (
              <div className="mt-2 p-3 bg-red-50 border border-red-200 rounded-md">
                <pre className="text-xs text-red-800 whitespace-pre-wrap break-words">
                  {item.exception}
                </pre>
              </div>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

export function Timeline({ items, className }: TimelineProps) {
  if (items.length === 0) {
    return (
      <div className={cn('text-center py-8 text-muted-foreground', className)}>
        No timeline data available
      </div>
    );
  }

  return (
    <div className={className}>
      {items.map((item, index) => (
        <TimelineItemComponent
          key={item.id}
          item={item}
          isLast={index === items.length - 1}
        />
      ))}
    </div>
  );
}
