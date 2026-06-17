import { useQuery } from '@tanstack/react-query';
import { apiService } from '@/services/api';
import { useRefetchInterval } from './useRefetchInterval';

export const useRateLimits = () => {
  const refetchInterval = useRefetchInterval();

  return useQuery({
    queryKey: ['rate-limits'],
    queryFn: async () => {
      const response = await apiService.getRateLimits();
      return response.data;
    },
    refetchInterval,
  });
};
