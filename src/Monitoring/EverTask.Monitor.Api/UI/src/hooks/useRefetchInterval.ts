import { useRefreshStore } from '@/stores/refreshStore';
import { useRealtimeStore } from '@/stores/realtimeStore';

/**
 * Hook that determines the appropriate refetch interval based on refresh mode and SignalR status.
 *
 * Returns:
 * - false: When SignalR is active (mode='signalr' && connected) or mode='disabled'
 * - 30000ms: When SignalR mode but disconnected (fallback polling)
 * - pollingInterval: When mode='polling' (user-configured interval)
 */
export const useRefetchInterval = (): number | false => {
  const mode = useRefreshStore((state) => state.mode);
  const pollingInterval = useRefreshStore((state) => state.pollingInterval);
  const connectionStatus = useRealtimeStore((state) => state.connectionStatus);

  // Disabled mode: no auto-refresh
  if (mode === 'disabled') {
    return false;
  }

  // Polling mode: use configured interval
  if (mode === 'polling') {
    return pollingInterval || 10000; // Default 10s if not set
  }

  // SignalR mode
  if (mode === 'signalr') {
    // If connected, disable polling (use SignalR events only)
    if (connectionStatus === 'connected') {
      return false;
    }
    // If disconnected or reconnecting, fallback to 30s polling
    return 30000;
  }

  // Fallback default
  return 10000;
};
