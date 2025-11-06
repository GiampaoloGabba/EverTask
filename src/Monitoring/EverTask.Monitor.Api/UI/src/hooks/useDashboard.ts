import { useQuery } from '@tanstack/react-query';
import { apiService } from '@/services/api';
import { DateRange } from '@/types/dashboard.types';
import { useRefetchInterval } from './useRefetchInterval';

export const useDashboardOverview = (range: DateRange = DateRange.Today) => {
  const refetchInterval = useRefetchInterval();

  return useQuery({
    queryKey: ['dashboard', 'overview', range],
    queryFn: async () => {
      const response = await apiService.getOverview(range);
      return response.data;
    },
    refetchInterval,
  });
};

export const useRecentActivity = (limit: number = 50) => {
  const refetchInterval = useRefetchInterval();

  return useQuery({
    queryKey: ['dashboard', 'recent-activity', limit],
    queryFn: async () => {
      const response = await apiService.getRecentActivity(limit);
      return response.data;
    },
    refetchInterval,
  });
};
