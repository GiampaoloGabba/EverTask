export interface RuntimeConfig {
  apiBasePath: string;
  uiBasePath: string;
  signalRHubPath: string;
  requireAuthentication: boolean;
  uiEnabled: boolean;
  eventDebounceMs: number;
}
