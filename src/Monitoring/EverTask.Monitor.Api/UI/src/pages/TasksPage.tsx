import { useState, useEffect } from 'react';
import { useTasks, useTaskCounts } from '@/hooks/useTasks';
import { useQueues } from '@/hooks/useQueues';
import { TasksTable } from '@/components/tasks/TasksTable';
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { Badge } from '@/components/ui/badge';
import { Input } from '@/components/ui/input';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { TaskFilter, QueuedTaskStatus, PaginationParams } from '@/types/task.types';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { AlertCircle, Search, RefreshCw } from 'lucide-react';

type TaskTab = 'all' | 'standard' | 'recurring' | 'failed';

const STORAGE_KEY_TAB = 'tasks-page-active-tab';
const STORAGE_KEY_REFRESH_INTERVAL = 'tasks-page-refresh-interval';

// Refresh interval options (in milliseconds, or false to disable)
const REFRESH_INTERVALS = [
  { value: '5000', label: '5 seconds' },
  { value: '10000', label: '10 seconds' },
  { value: '30000', label: '30 seconds' },
  { value: '60000', label: '1 minute' },
  { value: 'false', label: 'Disabled' },
];

// Status options for filter
const STATUS_OPTIONS = [
  { value: 'all', label: 'All Statuses' },
  { value: QueuedTaskStatus.WaitingQueue.toString(), label: 'Waiting Queue' },
  { value: QueuedTaskStatus.Queued.toString(), label: 'Queued' },
  { value: QueuedTaskStatus.InProgress.toString(), label: 'In Progress' },
  { value: QueuedTaskStatus.Pending.toString(), label: 'Pending' },
  { value: QueuedTaskStatus.Cancelled.toString(), label: 'Cancelled' },
  { value: QueuedTaskStatus.Completed.toString(), label: 'Completed' },
  { value: QueuedTaskStatus.Failed.toString(), label: 'Failed' },
  { value: QueuedTaskStatus.ServiceStopped.toString(), label: 'Service Stopped' },
];

export function TasksPage() {
  // Load from sessionStorage on mount
  const [activeTab, setActiveTab] = useState<TaskTab>(() => {
    const saved = sessionStorage.getItem(STORAGE_KEY_TAB);
    return (saved as TaskTab) || 'all';
  });

  const [statusFilter, setStatusFilter] = useState<string>('all');
  const [queueFilter, setQueueFilter] = useState<string>('all');
  const [searchTerm, setSearchTerm] = useState<string>('');

  // Refresh interval state (load from localStorage, default to 10 seconds)
  const [refreshInterval, setRefreshInterval] = useState<number | false>(() => {
    const saved = localStorage.getItem(STORAGE_KEY_REFRESH_INTERVAL);
    if (saved === 'false') return false;
    return saved ? parseInt(saved) : 10000;
  });

  // Save to sessionStorage when changed
  useEffect(() => {
    sessionStorage.setItem(STORAGE_KEY_TAB, activeTab);
  }, [activeTab]);

  // Save refresh interval to localStorage when changed
  useEffect(() => {
    localStorage.setItem(STORAGE_KEY_REFRESH_INTERVAL, String(refreshInterval));
  }, [refreshInterval]);

  const [pagination, setPagination] = useState<PaginationParams>({
    page: 1,
    pageSize: 20,
    sortBy: 'createdAtUtc',
    sortDescending: true,
  });

  // Apply tab-specific filters
  const getEffectiveFilters = (): TaskFilter => {
    const tabFilters: TaskFilter = {};

    // Apply tab filters
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

    // Apply inline filters (only if not in failed tab)
    if (activeTab !== 'failed' && statusFilter && statusFilter !== 'all') {
      tabFilters.statuses = [parseInt(statusFilter) as QueuedTaskStatus];
    }

    if (queueFilter && queueFilter !== 'all') {
      tabFilters.queueName = queueFilter;
    }

    if (searchTerm && searchTerm.trim()) {
      tabFilters.searchTerm = searchTerm.trim();
    }

    return tabFilters;
  };

  const effectiveFilters = getEffectiveFilters();
  const { data, isLoading, isError } = useTasks(effectiveFilters, pagination, {
    refetchInterval: refreshInterval,
  });
  const { data: counts } = useTaskCounts({ refetchInterval: refreshInterval });
  const { data: queues } = useQueues();

  const handlePageChange = (newPage: number) => {
    setPagination((prev) => ({ ...prev, page: newPage }));
  };

  const handleTabChange = (tab: string) => {
    const newTab = tab as TaskTab;
    setActiveTab(newTab);
    setPagination((prev) => ({ ...prev, page: 1 })); // Reset to first page
    // Only reset status filter (tabs already control status filtering)
    // Keep queue and search filters to work together with tab filters
    setStatusFilter('all');
    // DON'T reset: queueFilter and searchTerm - they work across all tabs
  };

  const handleStatusFilterChange = (value: string) => {
    setStatusFilter(value);
    setPagination((prev) => ({ ...prev, page: 1 }));
  };

  const handleQueueFilterChange = (value: string) => {
    setQueueFilter(value);
    setPagination((prev) => ({ ...prev, page: 1 }));
  };

  const handleSearchChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    setSearchTerm(e.target.value);
    setPagination((prev) => ({ ...prev, page: 1 }));
  };

  const handleRefreshIntervalChange = (value: string) => {
    if (value === 'false') {
      setRefreshInterval(false);
    } else {
      setRefreshInterval(parseInt(value));
    }
  };

  return (
    <div className="space-y-6">
      {/* Header with Refresh Control */}
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-3xl font-bold tracking-tight">Tasks</h1>
          <p className="text-muted-foreground">
            View and manage all background tasks
          </p>
        </div>

        {/* Auto-Refresh Interval */}
        <div className="flex items-center gap-2 min-w-[200px]">
          <span className="text-sm text-muted-foreground whitespace-nowrap">Auto-refresh:</span>
          <Select value={String(refreshInterval)} onValueChange={handleRefreshIntervalChange}>
            <SelectTrigger className="w-[150px] h-9">
              <div className="flex items-center gap-1.5 whitespace-nowrap">
                <RefreshCw className="h-3.5 w-3.5 flex-shrink-0" />
                <SelectValue />
              </div>
            </SelectTrigger>
            <SelectContent>
              {REFRESH_INTERVALS.map((option) => (
                <SelectItem key={option.value} value={option.value}>
                  {option.label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
      </div>

      {isError && (
        <Alert variant="destructive">
          <AlertCircle className="h-4 w-4" />
          <AlertDescription>
            Failed to load tasks. Please try refreshing the page.
          </AlertDescription>
        </Alert>
      )}

      {/* Tabs and Filters Row */}
      <div className="flex flex-col lg:flex-row lg:items-center lg:justify-between gap-4">
        <Tabs value={activeTab} onValueChange={handleTabChange} className="w-full lg:w-auto">
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
        </Tabs>

        {/* Inline Filters */}
        <div className="flex flex-col sm:flex-row gap-2 w-full lg:w-auto">
          {/* Status Filter (hidden in failed tab) */}
          {activeTab !== 'failed' && (
            <Select value={statusFilter} onValueChange={handleStatusFilterChange}>
              <SelectTrigger className="w-full sm:w-[180px]">
                <SelectValue placeholder="All Statuses" />
              </SelectTrigger>
              <SelectContent>
                {STATUS_OPTIONS.map((option) => (
                  <SelectItem key={option.value} value={option.value}>
                    {option.label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          )}

          {/* Queue Filter */}
          <Select value={queueFilter} onValueChange={handleQueueFilterChange}>
            <SelectTrigger className="w-full sm:w-[180px]">
              <SelectValue placeholder="All Queues" />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">All Queues</SelectItem>
              {queues?.map((queue) => (
                <SelectItem key={queue.queueName} value={queue.queueName}>
                  {queue.queueName}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>

          {/* Search */}
          <div className="relative w-full sm:w-[240px]">
            <Search className="absolute left-2 top-1/2 h-4 w-4 -translate-y-1/2 text-gray-500" />
            <Input
              type="text"
              placeholder="Search tasks..."
              value={searchTerm}
              onChange={handleSearchChange}
              className="pl-8"
            />
          </div>
        </div>
      </div>

      {/* Tasks Table */}
      <TasksTable
        tasks={data?.items || []}
        isLoading={isLoading}
        totalPages={data?.totalPages || 0}
        currentPage={pagination.page}
        onPageChange={handlePageChange}
      />
    </div>
  );
}
