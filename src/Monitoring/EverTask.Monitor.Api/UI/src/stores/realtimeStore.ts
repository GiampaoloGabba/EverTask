import { create } from 'zustand';
import { EverTaskEventData } from '@/types/signalr.types';

type ConnectionStatus = 'connected' | 'disconnected' | 'reconnecting';

interface RealtimeState {
  events: EverTaskEventData[];
  connectionStatus: ConnectionStatus;
  isPaused: boolean;
  addEvent: (event: EverTaskEventData) => void;
  clearEvents: () => void;
  setConnectionStatus: (status: ConnectionStatus) => void;
  togglePause: () => void;
}

export const useRealtimeStore = create<RealtimeState>((set) => ({
  events: [],
  connectionStatus: 'disconnected',
  isPaused: false,
  addEvent: (event) =>
    set((state) => {
      if (state.isPaused) return state;
      return {
        events: [event, ...state.events].slice(0, 200), // Keep last 200 events
      };
    }),
  clearEvents: () => set({ events: [] }),
  setConnectionStatus: (status) => set({ connectionStatus: status }),
  togglePause: () => set((state) => ({ isPaused: !state.isPaused })),
}));
