import { create } from 'zustand';
import { persist } from 'zustand/middleware';

export type RefreshMode = 'signalr' | 'polling' | 'disabled';

interface RefreshState {
  mode: RefreshMode;
  pollingInterval: number | false;
  lastRefreshTime: Date | null;
  setMode: (mode: RefreshMode) => void;
  setPollingInterval: (interval: number | false) => void;
  setLastRefreshTime: (time: Date) => void;
}

export const useRefreshStore = create<RefreshState>()(
  persist(
    (set) => ({
      mode: 'signalr',
      pollingInterval: 10000, // Default 10 seconds
      lastRefreshTime: null,
      setMode: (mode) => {
        set({ mode });
      },
      setPollingInterval: (interval) => {
        set({ pollingInterval: interval });
      },
      setLastRefreshTime: (time) => {
        set({ lastRefreshTime: time });
      },
    }),
    {
      name: 'evertask-refresh',
      partialize: (state) => ({
        mode: state.mode,
        pollingInterval: state.pollingInterval,
        // Don't persist lastRefreshTime
      }),
    }
  )
);
