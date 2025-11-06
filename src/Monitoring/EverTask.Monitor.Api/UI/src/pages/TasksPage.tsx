import { useState, useEffect } from 'react';
import { useTasks, useTaskCounts } from '@/hooks/useTasks';
import { TasksTable } from '@/components/tasks/TasksTable';
import { TaskFilters } from '@/components/tasks/TaskFilters';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { Badge } from '@/components/ui/badge';
import { TaskFilter, QueuedTaskStatus, PaginationParams } from '@/types/task.types';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { AlertCircle } from 'lucide-react';

type TaskTab = 'all' | 'standard' | 'recurring' | 'failed';

const STORAGE_KEY_TAB = 'tasks-page-active-tab';
const STORAGE_KEY_FILTERS = 'tasks-page-filters';

export function TasksPage() {
  // Load from sessionStorage on mount
  const [activeTab, setActiveTab] = useState<TaskTab>(() => {
    const saved = sessionStorage.getItem(STORAGE_KEY_TAB);
    return (saved as TaskTab) || 'all';
  });

  const [filters, setFilters] = useState<TaskFilter>(() => {
    const saved = sessionStorage.getItem(STORAGE_KEY_FILTERS);
    return saved ? JSON.parse(saved) : {};
  });

  // Save to sessionStorage when changed
  useEffect(() => {
    sessionStorage.setItem(STORAGE_KEY_TAB, activeTab);
  }, [activeTab]);

  useEffect(() => {
    sessionStorage.setItem(STORAGE_KEY_FILTERS, JSON.stringify(filters));
  }, [filters]);
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
      case 'recurring':
        tabFilters.isRecurring = true;
        delete tabFilters.statuses;
        break;
      case 'failed':
        tabFilters.statuses = [QueuedTaskStatus.Failed];
        delete tabFilters.isRecurring;
        break;
      case 'all':
      default:
        delete tabFilters.isRecurring;
        delete tabFilters.statuses;
        break;
    }

    return tabFilters;
  };

  const effectiveFilters = getEffectiveFilters();
  const { data, isLoading, isError } = useTasks(effectiveFilters, pagination);
  const { data: counts } = useTaskCounts();

  const handlePageChange = (newPage: number) => {
    setPagination((prev) => ({ ...prev, page: newPage }));
  };

  const handleFiltersChange = (newFilters: TaskFilter) => {
    setFilters(newFilters);
    setPagination((prev) => ({ ...prev, page: 1 })); // Reset to first page
  };

  const handleTabChange = (tab: string) => {
    const newTab = tab as TaskTab;
    setActiveTab(newTab);
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
          <TabsTrigger value="all">
            All Tasks
            {counts && counts.all > 0 && (
              <Badge variant="secondary" className="ml-2">
                {counts.all}
              </Badge>
            )}
          </TabsTrigger>
          <TabsTrigger value="standard">
            Standard
            {counts && counts.standard > 0 && (
              <Badge variant="secondary" className="ml-2">
                {counts.standard}
              </Badge>
            )}
          </TabsTrigger>
          <TabsTrigger value="recurring">
            Recurring
            {counts && counts.recurring > 0 && (
              <Badge variant="secondary" className="ml-2">
                {counts.recurring}
              </Badge>
            )}
          </TabsTrigger>
          <TabsTrigger value="failed">
            Failed
            {counts && counts.failed > 0 && (
              <Badge variant="destructive" className="ml-2">
                {counts.failed}
              </Badge>
            )}
          </TabsTrigger>
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
