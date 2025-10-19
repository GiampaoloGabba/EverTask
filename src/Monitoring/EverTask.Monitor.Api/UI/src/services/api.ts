import axios, { AxiosInstance, AxiosError } from 'axios';
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
    this.client.interceptors.request.use((config) => {
      const { username, password } = useAuthStore.getState();
      if (username && password) {
        const encoded = btoa(`${username}:${password}`);
        config.headers.Authorization = `Basic ${encoded}`;
      }
      return config;
    });

    // Handle 401 errors
    this.client.interceptors.response.use(
      (response) => response,
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
