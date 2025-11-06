import * as signalR from '@microsoft/signalr';
import { EverTaskEventData } from '@/types/signalr.types';
import { useRealtimeStore } from '@/stores/realtimeStore';
import { useAuthStore } from '@/stores/authStore';
import { configService } from './config';

class SignalRService {
  private connection: signalR.HubConnection | null = null;
  private hubPath: string = '';

  async initialize() {
    const config = await configService.fetchConfig();
    this.hubPath = config.signalRHubPath;
    await this.connect();
  }

  async connect() {
    if (this.connection) {
      await this.disconnect();
    }

    const { token } = useAuthStore.getState();

    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(this.hubPath, {
        accessTokenFactory: () => {
          // JWT token for SignalR
          return token || '';
        }
      })
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Information)
      .build();

    // Subscribe to EverTaskEvent
    this.connection.on('EverTaskEvent', (event: EverTaskEventData) => {
      useRealtimeStore.getState().addEvent(event);
    });

    this.connection.onreconnecting(() => {
      useRealtimeStore.getState().setConnectionStatus('reconnecting');
    });

    this.connection.onreconnected(() => {
      useRealtimeStore.getState().setConnectionStatus('connected');
    });

    this.connection.onclose(() => {
      useRealtimeStore.getState().setConnectionStatus('disconnected');
    });

    try {
      await this.connection.start();
      useRealtimeStore.getState().setConnectionStatus('connected');
    } catch (error) {
      console.error('SignalR connection failed:', error);
      useRealtimeStore.getState().setConnectionStatus('disconnected');
    }
  }

  async disconnect() {
    if (this.connection) {
      await this.connection.stop();
      this.connection = null;
    }
  }

  async start() {
    await this.initialize();
  }

  async stop() {
    await this.disconnect();
  }

  get connectionState() {
    return this.connection?.state ?? signalR.HubConnectionState.Disconnected;
  }
}

export const signalRService = new SignalRService();
