import axios, { AxiosInstance, AxiosError, InternalAxiosRequestConfig } from 'axios';
import { useAuthStore } from '@/stores/authStore';
import { configService } from './config';
import type {
  TaskFilter,
  PaginationParams,
  TasksPagedResponse,
  TaskDetailDto,
  StatusAuditDto,
  RunsAuditDto
} from '@/types/task.types';
import {
  DateRange,
  type OverviewDto,
  type RecentActivityDto
} from '@/types/dashboard.types';
import type { QueueMetricsDto } from '@/types/queue.types';
import {
  TimePeriod,
  type SuccessRateTrendDto,
  type ExecutionTimeDto
} from '@/types/statistics.types';

class ApiService {
  private client: AxiosInstance;
  private initialized = false;

  constructor() {
    this.client = axios.create();
    this.setupInterceptors();
  }

  async initialize() {
    if (this.initialized) return;

    const config = await configService.fetchConfig();
    this.client.defaults.baseURL = config.apiBasePath;
    this.initialized = true;
  }

  private setupInterceptors() {
    // Add Basic Auth header
    this.client.interceptors.request.use((config: InternalAxiosRequestConfig) => {
      const { username, password } = useAuthStore.getState();
      if (username && password) {
        const encoded = btoa(`${username}:${password}`);
        config.headers.Authorization = `Basic ${encoded}`;
      }
      return config;
    });

    // Convert status strings to numbers in responses
    this.client.interceptors.response.use(
      (response) => {
        // Convert status strings to enum numbers
        if (response.data) {
          this.convertStatusStringsToNumbers(response.data);
        }
        return response;
      },
      (error: AxiosError) => {
        if (error.response?.status === 401) {
          useAuthStore.getState().logout();
          // Redirect to login page
          window.location.href = '/evertask-dashboard/login';
        }
        return Promise.reject(error);
      }
    );
  }

  private convertStatusStringsToNumbers(obj: any): void {
    if (!obj || typeof obj !== 'object') return;

    const statusMap: Record<string, number> = {
      'WaitingQueue': 0,
      'Queued': 1,
      'InProgress': 2,
      'Pending': 3,
      'Cancelled': 4,
      'Completed': 5,
      'Failed': 6,
      'ServiceStopped': 7,
    };

    // Handle arrays
    if (Array.isArray(obj)) {
      obj.forEach(item => this.convertStatusStringsToNumbers(item));
      return;
    }

    // Convert statusDistribution object (keys are status strings)
    if ('statusDistribution' in obj && typeof obj.statusDistribution === 'object') {
      const converted: Record<number, number> = {};
      Object.entries(obj.statusDistribution).forEach(([key, value]) => {
        const numKey = statusMap[key] ?? parseInt(key);
        converted[numKey] = value as number;
      });
      obj.statusDistribution = converted;
    }

    // Convert status field if it's a string
    if ('status' in obj && typeof obj.status === 'string') {
      obj.status = statusMap[obj.status] ?? obj.status;
    }

    // Convert newStatus field if it's a string (for status audits)
    if ('newStatus' in obj && typeof obj.newStatus === 'string') {
      obj.newStatus = statusMap[obj.newStatus] ?? obj.newStatus;
    }

    // Recursively convert nested objects
    Object.keys(obj).forEach(key => {
      if (obj[key] && typeof obj[key] === 'object') {
        this.convertStatusStringsToNumbers(obj[key]);
      }
    });
  }

  // Tasks API
  async getTasks(filter: TaskFilter, pagination: PaginationParams) {
    await this.initialize();
    return this.client.get<TasksPagedResponse>('/tasks', {
      params: { ...filter, ...pagination }
    });
  }

  async getTaskDetail(id: string) {
    await this.initialize();
    return this.client.get<TaskDetailDto>(`/tasks/${id}`);
  }

  async getStatusAudit(id: string) {
    await this.initialize();
    return this.client.get<StatusAuditDto[]>(`/tasks/${id}/status-audit`);
  }

  async getRunsAudit(id: string) {
    await this.initialize();
    return this.client.get<RunsAuditDto[]>(`/tasks/${id}/runs-audit`);
  }

  // Dashboard API
  async getOverview(range: DateRange = DateRange.Today) {
    await this.initialize();
    return this.client.get<OverviewDto>('/dashboard/overview', {
      params: { range }
    });
  }

  async getRecentActivity(limit: number = 50) {
    await this.initialize();
    return this.client.get<RecentActivityDto[]>('/dashboard/recent-activity', {
      params: { limit }
    });
  }

  // Queues API
  async getQueues() {
    await this.initialize();
    return this.client.get<QueueMetricsDto[]>('/queues');
  }

  async getQueueTasks(name: string, pagination: PaginationParams) {
    await this.initialize();
    return this.client.get<TasksPagedResponse>(`/queues/${name}/tasks`, {
      params: pagination
    });
  }

  // Statistics API
  async getSuccessRateTrend(period: TimePeriod = TimePeriod.Last7Days) {
    await this.initialize();
    return this.client.get<SuccessRateTrendDto>('/statistics/success-rate-trend', {
      params: { period }
    });
  }

  async getTaskTypeDistribution(range: DateRange = DateRange.Week) {
    await this.initialize();
    return this.client.get<Record<string, number>>('/statistics/task-types', {
      params: { range }
    });
  }

  async getExecutionTimes(range: DateRange = DateRange.Today) {
    await this.initialize();
    return this.client.get<ExecutionTimeDto[]>('/statistics/execution-times', {
      params: { range }
    });
  }
}

export const apiService = new ApiService();
