import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { useSignalRRefresh } from '@/hooks/useSignalRRefresh';
import { configService } from '@/services/config';
import { useRealtimeStore } from '@/stores/realtimeStore';
import { useRefreshStore } from '@/stores/refreshStore';
import type { EverTaskEventData } from '@/types/signalr.types';

// The hook asks the backend for its throttle interval on mount; nothing here depends on the answer.
vi.mock('@/services/config', () => ({
  configService: { fetchConfig: vi.fn() },
}));

const event = (message: string, taskId = 'task-1'): EverTaskEventData => ({
  taskId,
  eventDateUtc: '2026-08-26T02:00:00Z',
  severity: 'Information',
  taskType: 'SampleTask',
  taskHandlerType: 'SampleTaskHandler',
  taskParameters: '{}',
  message,
  exception: null,
});

function setup() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const invalidate = vi.spyOn(client, 'invalidateQueries');

  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );

  const view = renderHook(() => useSignalRRefresh(), { wrapper });
  return { ...view, invalidate };
}

/** The query keys a spy on invalidateQueries has been handed, in call order. */
const invalidatedKeys = (invalidate: { mock: { calls: unknown[][] } }) =>
  invalidate.mock.calls.map((call) => JSON.stringify((call[0] as { queryKey: unknown[] }).queryKey));

describe('useSignalRRefresh', () => {
  beforeEach(() => {
    vi.mocked(configService.fetchConfig).mockResolvedValue({ eventDebounceMs: 1000 } as never);
    useRealtimeStore.setState({ events: [], connectionStatus: 'connected', isPaused: false });
    useRefreshStore.setState({ mode: 'signalr' });
  });

  afterEach(() => {
    useRealtimeStore.setState({ events: [] });
  });

  it('refreshes the occurrence list on any event, like the task counts', async () => {
    // The Occurrences tab declares no refetch interval and the client is 30s stale with no refetch on
    // focus, so without this invalidation the tab stays frozen on the snapshot it mounted with while every
    // other panel on the page updates. An occurrence is a task row: any event can create one or move it.
    const { invalidate } = setup();

    useRealtimeStore.getState().addEvent(event('Task materialized'));

    await waitFor(() => {
      const keys = invalidatedKeys(invalidate);
      expect(keys).toContain('["occurrences"]');
      expect(keys).toContain('["taskCounts"]');
    });
  });

  it('refreshes the row an event names', async () => {
    const { invalidate } = setup();

    useRealtimeStore.getState().addEvent(event('Task completed', 'task-42'));

    await waitFor(() => expect(invalidatedKeys(invalidate)).toContain('["task","task-42"]'));
  });

  it('refreshes the lists a status change moves', async () => {
    const { invalidate } = setup();

    useRealtimeStore.getState().addEvent(event('Task completed'));

    await waitFor(() => {
      const keys = invalidatedKeys(invalidate);
      expect(keys).toContain('["tasks"]');
      expect(keys).toContain('["dashboard"]');
    });
  });

  it('leaves the wider lists alone for an event that moves no status', async () => {
    const { invalidate } = setup();

    useRealtimeStore.getState().addEvent(event('Rate limit deferred task'));

    await waitFor(() => expect(invalidatedKeys(invalidate)).toContain('["occurrences"]'));
    expect(invalidatedKeys(invalidate)).not.toContain('["statistics"]');
  });

  it('does nothing at all while the refresh mode is not SignalR', async () => {
    useRefreshStore.setState({ mode: 'polling' });
    const { invalidate } = setup();

    useRealtimeStore.getState().addEvent(event('Task completed'));

    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(invalidate).not.toHaveBeenCalled();
  });
});
