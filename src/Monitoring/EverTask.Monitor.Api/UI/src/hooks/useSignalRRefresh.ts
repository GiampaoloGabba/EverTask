import { useEffect, useRef, useCallback } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useRealtimeStore } from '@/stores/realtimeStore';
import { useRefreshStore } from '@/stores/refreshStore';
import { EverTaskEventData } from '@/types/signalr.types';
import { configService } from '@/services/config';

/**
 * Hook that listens to SignalR events and intelligently invalidates TanStack Query cache
 * with throttling to prevent excessive API calls during task event bursts.
 *
 * Throttling ensures the first event executes immediately, then blocks subsequent
 * executions for the configured interval. This provides regular, predictable updates
 * even during continuous task activity.
 *
 * Also provides fallback to polling when SignalR is disconnected.
 */
export const useSignalRRefresh = () => {
  const queryClient = useQueryClient();
  const connectionStatus = useRealtimeStore((state) => state.connectionStatus);
  const events = useRealtimeStore((state) => state.events);
  const mode = useRefreshStore((state) => state.mode);
  const setLastRefreshTime = useRefreshStore((state) => state.setLastRefreshTime);

  const isThrottledRef = useRef(false);
  const throttleMs = useRef<number>(1000);
  const pendingEventsRef = useRef<EverTaskEventData[]>([]);

  // Load throttle config from backend (parameter named EventDebounceMs for API compatibility)
  useEffect(() => {
    configService.fetchConfig().then((config) => {
      throttleMs.current = config.eventDebounceMs;
    }).catch((error) => {
      console.error('Failed to load throttle config:', error);
    });
  }, []);

  // Handle cache invalidation based on events
  const invalidateCache = useCallback((events: EverTaskEventData[]) => {
    if (events.length === 0) return;

    // Always invalidate task counts (affects all pages)
    queryClient.invalidateQueries({ queryKey: ['taskCounts'] });

    // Process each event
    events.forEach((event) => {
      // If we have a specific taskId, invalidate that task's detail
      if (event.taskId) {
        queryClient.invalidateQueries({ queryKey: ['task', event.taskId] });
      }

      // Check if this is a status change event (started, completed, failed, etc.)
      const statusKeywords = ['started', 'completed', 'failed', 'cancelled', 'queued', 'pending'];
      const isStatusChange = statusKeywords.some((keyword) =>
        event.message.toLowerCase().includes(keyword)
      );

      if (isStatusChange) {
        // Invalidate all data that depends on task status/counts
        queryClient.invalidateQueries({ queryKey: ['tasks'] });
        queryClient.invalidateQueries({ queryKey: ['queues'] });
        queryClient.invalidateQueries({ queryKey: ['dashboard'] });
        queryClient.invalidateQueries({ queryKey: ['statistics'] });
      }
    });

    // Update last refresh time
    setLastRefreshTime(new Date());
  }, [queryClient, setLastRefreshTime]);

  // Throttled handler - executes immediately, then blocks for throttleMs
  const handleEventsThrottled = useCallback(() => {
    if (!isThrottledRef.current) {
      // NOT in throttle period - execute immediately
      invalidateCache([...pendingEventsRef.current]);
      pendingEventsRef.current = [];

      // Activate throttle period
      isThrottledRef.current = true;

      // After throttleMs, deactivate throttle and execute pending events if any
      setTimeout(() => {
        isThrottledRef.current = false;

        // If events accumulated during throttle period, execute them now
        if (pendingEventsRef.current.length > 0) {
          invalidateCache([...pendingEventsRef.current]);
          pendingEventsRef.current = [];
        }
      }, throttleMs.current);
    }
    // Otherwise we're in throttle period - events are just accumulated in pendingEventsRef
  }, [invalidateCache]);

  // Listen to SignalR events only if mode is 'signalr'
  useEffect(() => {
    if (mode !== 'signalr' || events.length === 0) {
      return;
    }

    // Get the most recent event (events[0] is the newest)
    const latestEvent = events[0];

    // Add to pending events queue
    pendingEventsRef.current.push(latestEvent);

    // Trigger throttled handler
    handleEventsThrottled();
  }, [events, mode, handleEventsThrottled]);

  // Determine if SignalR is active
  const isSignalRActive = mode === 'signalr' && connectionStatus === 'connected';

  // Determine if we should use polling fallback
  const shouldFallbackToPolling = mode === 'signalr' && connectionStatus !== 'connected';

  return {
    isSignalRActive,
    connectionStatus,
    shouldFallbackToPolling,
    isRefreshing: isThrottledRef.current || pendingEventsRef.current.length > 0,
  };
};
