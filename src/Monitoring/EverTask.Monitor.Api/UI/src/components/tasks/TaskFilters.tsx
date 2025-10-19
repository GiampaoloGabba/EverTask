import { useState } from 'react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { Card, CardContent } from '@/components/ui/card';
import { TaskFilter, QueuedTaskStatus } from '@/types/task.types';
import { Filter, X } from 'lucide-react';

interface TaskFiltersProps {
  filters: TaskFilter;
  onFiltersChange: (filters: TaskFilter) => void;
  availableQueues?: string[];
}

export function TaskFilters({ filters, onFiltersChange, availableQueues = [] }: TaskFiltersProps) {
  const [localFilters, setLocalFilters] = useState<TaskFilter>(filters);
  const [isExpanded, setIsExpanded] = useState(false);

  const handleApply = () => {
    onFiltersChange(localFilters);
    setIsExpanded(false);
  };

  const handleClear = () => {
    const emptyFilters: TaskFilter = {};
    setLocalFilters(emptyFilters);
    onFiltersChange(emptyFilters);
  };

  const hasActiveFilters = Object.keys(filters).length > 0;

  return (
    <Card>
      <CardContent className="pt-6">
        <div className="space-y-4">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              <Filter className="h-4 w-4 text-muted-foreground" />
              <span className="font-medium">Filters</span>
              {hasActiveFilters && (
                <span className="text-xs bg-blue-100 text-blue-700 px-2 py-1 rounded">
                  {Object.keys(filters).length} active
                </span>
              )}
            </div>
            <div className="flex gap-2">
              {hasActiveFilters && (
                <Button variant="ghost" size="sm" onClick={handleClear}>
                  <X className="h-4 w-4 mr-1" />
                  Clear
                </Button>
              )}
              <Button
                variant="outline"
                size="sm"
                onClick={() => setIsExpanded(!isExpanded)}
              >
                {isExpanded ? 'Hide' : 'Show'} Filters
              </Button>
            </div>
          </div>

          {isExpanded && (
            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4 pt-4 border-t">
              <div className="space-y-2">
                <Label htmlFor="status">Status</Label>
                <Select
                  value={localFilters.statuses?.[0]?.toString() || '__all__'}
                  onValueChange={(value) =>
                    setLocalFilters({
                      ...localFilters,
                      statuses: value === '__all__' ? undefined : [Number(value) as QueuedTaskStatus],
                    })
                  }
                >
                  <SelectTrigger id="status">
                    <SelectValue placeholder="All statuses" />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="__all__">All statuses</SelectItem>
                    <SelectItem value={QueuedTaskStatus.WaitingQueue.toString()}>Waiting</SelectItem>
                    <SelectItem value={QueuedTaskStatus.Queued.toString()}>Queued</SelectItem>
                    <SelectItem value={QueuedTaskStatus.InProgress.toString()}>In Progress</SelectItem>
                    <SelectItem value={QueuedTaskStatus.Pending.toString()}>Pending</SelectItem>
                    <SelectItem value={QueuedTaskStatus.Completed.toString()}>Completed</SelectItem>
                    <SelectItem value={QueuedTaskStatus.Failed.toString()}>Failed</SelectItem>
                    <SelectItem value={QueuedTaskStatus.Cancelled.toString()}>Cancelled</SelectItem>
                  </SelectContent>
                </Select>
              </div>

              <div className="space-y-2">
                <Label htmlFor="taskType">Task Type</Label>
                <Input
                  id="taskType"
                  placeholder="Search task type..."
                  value={localFilters.taskType || ''}
                  onChange={(e) =>
                    setLocalFilters({
                      ...localFilters,
                      taskType: e.target.value || undefined,
                    })
                  }
                />
              </div>

              <div className="space-y-2">
                <Label htmlFor="queueName">Queue</Label>
                <Select
                  value={localFilters.queueName || '__all__'}
                  onValueChange={(value) =>
                    setLocalFilters({
                      ...localFilters,
                      queueName: value === '__all__' ? undefined : value,
                    })
                  }
                >
                  <SelectTrigger id="queueName">
                    <SelectValue placeholder="All queues" />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="__all__">All queues</SelectItem>
                    <SelectItem value="null">Default Queue</SelectItem>
                    {availableQueues.map((queue) => (
                      <SelectItem key={queue} value={queue}>
                        {queue}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>

              <div className="space-y-2">
                <Label htmlFor="searchTerm">Search</Label>
                <Input
                  id="searchTerm"
                  placeholder="Search tasks..."
                  value={localFilters.searchTerm || ''}
                  onChange={(e) =>
                    setLocalFilters({
                      ...localFilters,
                      searchTerm: e.target.value || undefined,
                    })
                  }
                />
              </div>

              <div className="space-y-2">
                <Label htmlFor="isRecurring">Recurring</Label>
                <Select
                  value={localFilters.isRecurring?.toString() || '__all__'}
                  onValueChange={(value) =>
                    setLocalFilters({
                      ...localFilters,
                      isRecurring: value === 'true' ? true : value === 'false' ? false : undefined,
                    })
                  }
                >
                  <SelectTrigger id="isRecurring">
                    <SelectValue placeholder="All tasks" />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="__all__">All tasks</SelectItem>
                    <SelectItem value="true">Recurring only</SelectItem>
                    <SelectItem value="false">Non-recurring only</SelectItem>
                  </SelectContent>
                </Select>
              </div>

              <div className="flex items-end">
                <Button onClick={handleApply} className="w-full">
                  Apply Filters
                </Button>
              </div>
            </div>
          )}
        </div>
      </CardContent>
    </Card>
  );
}
