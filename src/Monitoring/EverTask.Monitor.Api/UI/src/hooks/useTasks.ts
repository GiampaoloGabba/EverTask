import { useQuery, UseQueryOptions } from '@tanstack/react-query';
import { apiService } from '@/services/api';
import {
  TaskFilter,
  PaginationParams,
  TasksPagedResponse,
  TaskDetailDto,
  TaskCountsDto
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
