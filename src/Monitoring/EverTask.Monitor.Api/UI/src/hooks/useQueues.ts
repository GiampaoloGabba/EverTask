import { useQuery } from '@tanstack/react-query';
import { apiService } from '@/services/api';
import { useRefetchInterval } from './useRefetchInterval';

export const useQueues = () => {
  const refetchInterval = useRefetchInterval();

  return useQuery({
    queryKey: ['queues'],
    queryFn: async () => {
      const response = await apiService.getQueues();
      return response.data;
    },
    refetchInterval,
  });
};
