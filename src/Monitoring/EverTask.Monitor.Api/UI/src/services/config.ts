import { RuntimeConfig } from '@/types/config.types';

export class ConfigService {
  private config: RuntimeConfig | null = null;

  async fetchConfig(): Promise<RuntimeConfig> {
    if (this.config) {
      return this.config;
    }

    // Fetch from backend /monitoring/api/config endpoint (no auth required)
    // Base path is fixed to /monitoring (not configurable)
    const response = await fetch('/monitoring/api/config');
    if (!response.ok) {
      throw new Error('Failed to fetch runtime configuration');
    }

    this.config = await response.json();
    return this.config as RuntimeConfig;
  }

  getConfig(): RuntimeConfig | null {
    return this.config;
  }
}

export const configService = new ConfigService();
