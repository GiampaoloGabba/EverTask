import { useQuery, UseQueryOptions } from '@tanstack/react-query';
import { apiService } from '@/services/api';
import {
  TaskFilter,
  PaginationParams,
  TasksPagedResponse,
  TaskDetailDto,
  TaskCountsDto,
  OccurrencesResponse,
  StatusAuditsResponse,
  RunsAuditsResponse
} from '@/types/task.types';

export const useTasks = (
  filter: TaskFilter,
  pagination: PaginationParams,
  options?: Omit<UseQueryOptions<TasksPagedResponse>, 'queryKey' | 'queryFn'>
) => {
  return useQuery({
    queryKey: ['tasks', filter, pagination],
    queryFn: async () => {
      const response = await apiService.getTasks(filter, pagination);
      return response.data;
    },
    ...options,
  });
};

export const useTaskDetail = (
  id: string,
  options?: Omit<UseQueryOptions<TaskDetailDto>, 'queryKey' | 'queryFn'>
) => {
  return useQuery({
    queryKey: ['task', id],
    queryFn: async () => {
      const response = await apiService.getTaskDetail(id);
      return response.data;
    },
    enabled: !!id,
    ...options,
  });
};

export const useTaskCounts = (
  options?: Omit<UseQueryOptions<TaskCountsDto>, 'queryKey' | 'queryFn'>
) => {
  return useQuery({
    queryKey: ['taskCounts'],
    queryFn: async () => {
      const response = await apiService.getTaskCounts();
      return response.data;
    },
    ...options,
  });
};

/**
 * One page of the occurrences of a schedule. The paging is the SERVER's — a schedule with a long retention
 * behind it holds hundreds of thousands of rows — so `skip` belongs to the query key: two pages are two
 * different answers, not the same one filtered.
 */
export const useOccurrences = (
  scheduleId: string,
  enabled: boolean,
  skip: number = 0,
  take: number = 100,
  options?: Omit<UseQueryOptions<OccurrencesResponse>, 'queryKey' | 'queryFn'>
) => {
  return useQuery({
    queryKey: ['occurrences', scheduleId, skip, take],
    queryFn: async () => {
      const response = await apiService.getOccurrences(scheduleId, skip, take);
      return response.data;
    },
    enabled: enabled && !!scheduleId,
    ...options,
  });
};

/**
 * One page of a task's status transitions. The paging is the SERVER's — a long-lived recurring row records
 * one transition per state per run — so `skip` belongs to the query key, exactly as it does for the
 * occurrences above: two pages are two different answers, not the same one filtered.
 */
export const useStatusAudits = (
  taskId: string,
  skip: number = 0,
  take: number = 100,
  options?: Omit<UseQueryOptions<StatusAuditsResponse>, 'queryKey' | 'queryFn'>
) => {
  return useQuery({
    queryKey: ['statusAudits', taskId, skip, take],
    queryFn: async () => {
      const response = await apiService.getStatusAudit(taskId, skip, take);
      return response.data;
    },
    enabled: !!taskId,
    ...options,
  });
};

/** One page of a task's recorded runs, paged by the server for the same reason. */
export const useRunsAudits = (
  taskId: string,
  skip: number = 0,
  take: number = 100,
  options?: Omit<UseQueryOptions<RunsAuditsResponse>, 'queryKey' | 'queryFn'>
) => {
  return useQuery({
    queryKey: ['runsAudits', taskId, skip, take],
    queryFn: async () => {
      const response = await apiService.getRunsAudit(taskId, skip, take);
      return response.data;
    },
    enabled: !!taskId,
    ...options,
  });
};
