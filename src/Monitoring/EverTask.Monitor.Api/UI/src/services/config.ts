import { RuntimeConfig } from '@/types/config.types';

export class ConfigService {
  private config: RuntimeConfig | null = null;

  async fetchConfig(): Promise<RuntimeConfig> {
    if (this.config) {
      return this.config;
    }

    // Fetch from backend /config endpoint (no auth required)
    // This will work regardless of the base path configuration
    const response = await fetch('/evertask/api/config');
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
