import { create } from 'zustand';
import { RuntimeConfig } from '@/types/config.types';

interface ConfigState {
  config: RuntimeConfig | null;
  setConfig: (config: RuntimeConfig) => void;
}

export const useConfigStore = create<ConfigState>((set) => ({
  config: null,
  setConfig: (config) => set({ config }),
}));
