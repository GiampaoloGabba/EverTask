import { useState } from 'react';
import { useTasks } from '@/hooks/useTasks';
import { TasksTable } from '@/components/tasks/TasksTable';
import { TaskFilters } from '@/components/tasks/TaskFilters';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { TaskFilter, QueuedTaskStatus, PaginationParams } from '@/types/task.types';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { AlertCircle } from 'lucide-react';

type TaskTab = 'all' | 'standard' | 'delayed' | 'recurring' | 'failed';

export function TasksPage() {
  const [activeTab, setActiveTab] = useState<TaskTab>('all');
  const [filters, setFilters] = useState<TaskFilter>({});
  const [pagination, setPagination] = useState<PaginationParams>({
    page: 1,
    pageSize: 20,
    sortBy: 'createdAtUtc',
    sortDescending: true,
  });

  // Apply tab-specific filters
  const getEffectiveFilters = (): TaskFilter => {
    const tabFilters: TaskFilter = { ...filters };

    switch (activeTab) {
      case 'standard':
        tabFilters.isRecurring = false;
        delete tabFilters.statuses;
        break;
      case 'delayed':
        tabFilters.isRecurring = false;
        // Note: We can't directly filter by scheduledExecutionUtc != null in the filter,
        // but the backend should handle this based on the context
        break;
      case 'recurring':
        tabFilters.isRecurring = true;
        break;
      case 'failed':
        tabFilters.statuses = [QueuedTaskStatus.Failed];
        break;
      case 'all':
      default:
        // No additional filters
        break;
    }

    return tabFilters;
  };

  const effectiveFilters = getEffectiveFilters();
  const { data, isLoading, isError } = useTasks(effectiveFilters, pagination);

  const handlePageChange = (newPage: number) => {
    setPagination((prev) => ({ ...prev, page: newPage }));
  };

  const handleFiltersChange = (newFilters: TaskFilter) => {
    setFilters(newFilters);
    setPagination((prev) => ({ ...prev, page: 1 })); // Reset to first page
  };

  const handleTabChange = (tab: string) => {
    setActiveTab(tab as TaskTab);
    setPagination((prev) => ({ ...prev, page: 1 })); // Reset to first page
  };

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-3xl font-bold tracking-tight">Tasks</h1>
        <p className="text-muted-foreground">
          View and manage all background tasks
        </p>
      </div>

      <TaskFilters
        filters={filters}
        onFiltersChange={handleFiltersChange}
      />

      {isError && (
        <Alert variant="destructive">
          <AlertCircle className="h-4 w-4" />
          <AlertDescription>
            Failed to load tasks. Please try refreshing the page.
          </AlertDescription>
        </Alert>
      )}

      <Tabs value={activeTab} onValueChange={handleTabChange}>
        <TabsList>
          <TabsTrigger value="all">All Tasks</TabsTrigger>
          <TabsTrigger value="standard">Standard</TabsTrigger>
          <TabsTrigger value="delayed">Delayed</TabsTrigger>
          <TabsTrigger value="recurring">Recurring</TabsTrigger>
          <TabsTrigger value="failed">Failed</TabsTrigger>
        </TabsList>

        <TabsContent value={activeTab} className="mt-6">
          <TasksTable
            tasks={data?.items || []}
            isLoading={isLoading}
            totalPages={data?.totalPages || 0}
            currentPage={pagination.page}
            onPageChange={handlePageChange}
          />
        </TabsContent>
      </Tabs>
    </div>
  );
}
