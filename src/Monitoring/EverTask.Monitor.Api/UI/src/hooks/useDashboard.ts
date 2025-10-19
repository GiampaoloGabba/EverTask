import { useQuery } from '@tanstack/react-query';
import { apiService } from '@/services/api';
import { DateRange } from '@/types/dashboard.types';

export const useDashboardOverview = (range: DateRange = DateRange.Today) => {
  return useQuery({
    queryKey: ['dashboard', 'overview', range],
    queryFn: async () => {
      const response = await apiService.getOverview(range);
      return response.data;
    },
    refetchInterval: 30000, // Refresh every 30 seconds
  });
};

export const useRecentActivity = (limit: number = 50) => {
  return useQuery({
    queryKey: ['dashboard', 'recent-activity', limit],
    queryFn: async () => {
      const response = await apiService.getRecentActivity(limit);
      return response.data;
    },
    refetchInterval: 10000, // Refresh every 10 seconds
  });
};
