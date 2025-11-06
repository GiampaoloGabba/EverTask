import { useNavigate } from 'react-router-dom';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { TaskStatusBadge } from '@/components/common/TaskStatusBadge';
import { EmptyState } from '@/components/common/EmptyState';
import { TaskListDto } from '@/types/task.types';
import { format } from 'date-fns';
import { ChevronLeft, ChevronRight, ListX, Clock } from 'lucide-react';

interface TasksTableProps {
  tasks: TaskListDto[];
  isLoading: boolean;
  totalPages: number;
  currentPage: number;
  onPageChange: (page: number) => void;
  onSort?: (sortBy: string) => void;
}

export function TasksTable({
  tasks,
  isLoading,
  totalPages,
  currentPage,
  onPageChange,
}: TasksTableProps) {
  const navigate = useNavigate();

  const formatHandler = (type: string) => {
    // Extract short name from type
    const parts = type.split('.');
    return parts[parts.length - 1] || type;
  };

  const formatDate = (dateStr: string | null) => {
    if (!dateStr) return '-';
    try {
      return format(new Date(dateStr), 'MMM d, yyyy HH:mm');
    } catch {
      return '-';
    }
  };

  const formatExecutionTime = (ms: number) => {
    if (ms === 0) return '-';
    if (ms < 1000) return `${ms.toFixed(0)}ms`;
    return `${(ms / 1000).toFixed(2)}s`;
  };

  if (isLoading) {
    return (
      <div className="space-y-3">
        {[...Array(5)].map((_, i) => (
          <Skeleton key={i} className="h-16 w-full" />
        ))}
      </div>
    );
  }

  if (tasks.length === 0) {
    return (
      <EmptyState
        icon={ListX}
        title="No tasks found"
        description="There are no tasks matching your current filters. Try adjusting your search criteria."
      />
    );
  }

  return (
    <div className="space-y-4">
      <div className="rounded-md border bg-white">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Status</TableHead>
              <TableHead>Type</TableHead>
              <TableHead>Queue</TableHead>
              <TableHead>Created At</TableHead>
              <TableHead>Last Execution</TableHead>
              <TableHead>Duration</TableHead>
              <TableHead>Scheduled</TableHead>
              <TableHead>Info</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {tasks.map((task) => (
              <TableRow
                key={task.id}
                className="cursor-pointer hover:bg-gray-50"
                onClick={() => navigate(`/tasks/${task.id}`)}
              >
                <TableCell>
                  <TaskStatusBadge status={task.status} />
                </TableCell>
                <TableCell>
                  <div className="max-w-[200px]">
                    <p className="font-medium text-sm truncate" title={task.type}>
                      {formatHandler(task.type)}
                    </p>
                  </div>
                </TableCell>
                <TableCell>
                  <span className="text-sm text-muted-foreground">
                    {task.queueName || 'Default'}
                  </span>
                </TableCell>
                <TableCell>
                  <span className="text-sm">{formatDate(task.createdAtUtc)}</span>
                </TableCell>
                <TableCell>
                  <span className="text-sm">{formatDate(task.lastExecutionUtc)}</span>
                </TableCell>
                <TableCell>
                  <span className="text-sm font-mono text-gray-700">
                    {formatExecutionTime(task.executionTimeMs)}
                  </span>
                </TableCell>
                <TableCell>
                  {task.scheduledExecutionUtc ? (
                    <div className="flex items-center gap-1 text-sm text-purple-600">
                      <Clock className="h-3 w-3" />
                      <span>{formatDate(task.scheduledExecutionUtc)}</span>
                    </div>
                  ) : (
                    <span className="text-sm text-muted-foreground">-</span>
                  )}
                </TableCell>
                <TableCell>
                  <div className="flex gap-1 flex-wrap">
                    {task.isRecurring && (
                      <Badge variant="outline" className="text-xs bg-purple-50 text-purple-700 border-purple-200">
                        Recurring
                      </Badge>
                    )}
                    {task.taskKey && (
                      <Badge variant="outline" className="text-xs bg-blue-50 text-blue-700 border-blue-200" title={task.taskKey}>
                        Key: {task.taskKey.length > 10 ? `${task.taskKey.substring(0, 10)}...` : task.taskKey}
                      </Badge>
                    )}
                    {task.maxRuns && task.currentRunCount !== null && (
                      <Badge variant="outline" className="text-xs">
                        {task.currentRunCount} / {task.maxRuns}
                      </Badge>
                    )}
                  </div>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </div>

      {/* Pagination */}
      {totalPages > 1 && (
        <div className="flex items-center justify-between">
          <div className="text-sm text-muted-foreground">
            Page {currentPage} of {totalPages}
          </div>
          <div className="flex gap-2">
            <Button
              variant="outline"
              size="sm"
              onClick={() => onPageChange(currentPage - 1)}
              disabled={currentPage === 1}
            >
              <ChevronLeft className="h-4 w-4 mr-1" />
              Previous
            </Button>
            <Button
              variant="outline"
              size="sm"
              onClick={() => onPageChange(currentPage + 1)}
              disabled={currentPage === totalPages}
            >
              Next
              <ChevronRight className="h-4 w-4 ml-1" />
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}
