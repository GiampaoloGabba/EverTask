import { TaskStatusBadge } from './TaskStatusBadge';
import { QueuedTaskStatus } from '@/types/task.types';
import { formatDistanceToNow } from 'date-fns';
import { ChevronDown, ChevronRight, Copy } from 'lucide-react';
import { useState } from 'react';
import { cn } from '@/lib/utils';
import { Button } from '@/components/ui/button';

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
  const [copied, setCopied] = useState(false);
  const hasException = item.exception && item.exception.trim() !== '';

  const handleCopyException = () => {
    if (item.exception) {
      navigator.clipboard.writeText(item.exception);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    }
  };

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
            <div className="flex items-center justify-between mb-2">
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
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={handleCopyException}
                  className="h-6 text-xs"
                >
                  <Copy className={copied ? 'h-3 w-3 mr-1 text-green-600' : 'h-3 w-3 mr-1'} />
                  {copied ? 'Copied!' : 'Copy'}
                </Button>
              )}
            </div>
            {isExpanded && (
              <div className="relative mt-2 p-3 bg-red-50 dark:bg-red-950 border border-red-200 dark:border-red-800 rounded-md">
                <pre className="text-xs font-mono text-red-900 dark:text-red-100 whitespace-pre-wrap break-words overflow-x-auto">
                  {item.exception}
                </pre>
              </div>
            )}
            {!isExpanded && item.exception && (
              <div className="mt-2 p-3 bg-red-50 dark:bg-red-950 border border-red-200 dark:border-red-800 rounded-md">
                <pre className="text-xs font-mono text-red-900 dark:text-red-100 whitespace-pre-wrap break-words line-clamp-2">
                  {item.exception.split('\n').slice(0, 2).join('\n')}
                </pre>
                <p className="text-xs text-muted-foreground mt-2">
                  Click to expand full exception
                </p>
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
