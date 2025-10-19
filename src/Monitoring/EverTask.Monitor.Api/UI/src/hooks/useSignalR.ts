import { useEffect } from 'react';
import { signalRService } from '@/services/signalr';
import { useRealtimeStore } from '@/stores/realtimeStore';

export const useSignalR = () => {
  const connectionStatus = useRealtimeStore((state) => state.connectionStatus);

  useEffect(() => {
    signalRService.initialize();

    return () => {
      signalRService.disconnect();
    };
  }, []);

  return { connectionStatus };
};
