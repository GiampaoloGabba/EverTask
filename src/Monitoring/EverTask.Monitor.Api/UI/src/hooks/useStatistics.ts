import { useQuery } from '@tanstack/react-query';
import { apiService } from '@/services/api';
import { TimePeriod } from '@/types/statistics.types';
import { DateRange } from '@/types/dashboard.types';

export const useSuccessRateTrend = (period: TimePeriod = TimePeriod.Last7Days) => {
  return useQuery({
    queryKey: ['statistics', 'success-rate-trend', period],
    queryFn: async () => {
      const response = await apiService.getSuccessRateTrend(period);
      return response.data;
    },
  });
};

export const useTaskTypeDistribution = (range: DateRange = DateRange.Week) => {
  return useQuery({
    queryKey: ['statistics', 'task-types', range],
    queryFn: async () => {
      const response = await apiService.getTaskTypeDistribution(range);
      return response.data;
    },
  });
};

export const useExecutionTimes = (range: DateRange = DateRange.Today) => {
  return useQuery({
    queryKey: ['statistics', 'execution-times', range],
    queryFn: async () => {
      const response = await apiService.getExecutionTimes(range);
      return response.data;
    },
  });
};
