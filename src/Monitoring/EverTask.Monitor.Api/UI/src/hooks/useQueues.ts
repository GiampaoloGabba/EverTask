import { useQuery } from '@tanstack/react-query';
import { apiService } from '@/services/api';

export const useQueues = () => {
  return useQuery({
    queryKey: ['queues'],
    queryFn: async () => {
      const response = await apiService.getQueues();
      return response.data;
    },
    refetchInterval: 30000,
  });
};
