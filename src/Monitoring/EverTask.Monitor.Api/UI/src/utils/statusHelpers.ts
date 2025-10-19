import { QueuedTaskStatus } from '@/types/task.types';

export const getStatusColor = (status: QueuedTaskStatus): string => {
  switch (status) {
    case QueuedTaskStatus.WaitingQueue:
    case QueuedTaskStatus.Queued:
      return 'bg-blue-500';
    case QueuedTaskStatus.InProgress:
      return 'bg-yellow-500';
    case QueuedTaskStatus.Pending:
      return 'bg-purple-500';
    case QueuedTaskStatus.Completed:
      return 'bg-green-500';
    case QueuedTaskStatus.Failed:
      return 'bg-red-500';
    case QueuedTaskStatus.Cancelled:
    case QueuedTaskStatus.ServiceStopped:
      return 'bg-gray-500';
    default:
      return 'bg-gray-500';
  }
};

export const getStatusLabel = (status: QueuedTaskStatus): string => {
  switch (status) {
    case QueuedTaskStatus.WaitingQueue:
      return 'Waiting';
    case QueuedTaskStatus.Queued:
      return 'Queued';
    case QueuedTaskStatus.InProgress:
      return 'In Progress';
    case QueuedTaskStatus.Pending:
      return 'Pending';
    case QueuedTaskStatus.Cancelled:
      return 'Cancelled';
    case QueuedTaskStatus.Completed:
      return 'Completed';
    case QueuedTaskStatus.Failed:
      return 'Failed';
    case QueuedTaskStatus.ServiceStopped:
      return 'Service Stopped';
    default:
      return 'Unknown';
  }
};

export const getStatusTextColor = (status: QueuedTaskStatus): string => {
  switch (status) {
    case QueuedTaskStatus.WaitingQueue:
    case QueuedTaskStatus.Queued:
      return 'text-blue-700';
    case QueuedTaskStatus.InProgress:
      return 'text-yellow-700';
    case QueuedTaskStatus.Pending:
      return 'text-purple-700';
    case QueuedTaskStatus.Completed:
      return 'text-green-700';
    case QueuedTaskStatus.Failed:
      return 'text-red-700';
    case QueuedTaskStatus.Cancelled:
    case QueuedTaskStatus.ServiceStopped:
      return 'text-gray-700';
    default:
      return 'text-gray-700';
  }
};

export const getSeverityColor = (severity: 'Information' | 'Warning' | 'Error'): string => {
  switch (severity) {
    case 'Information':
      return 'bg-blue-100 text-blue-800 border-blue-200';
    case 'Warning':
      return 'bg-yellow-100 text-yellow-800 border-yellow-200';
    case 'Error':
      return 'bg-red-100 text-red-800 border-red-200';
    default:
      return 'bg-gray-100 text-gray-800 border-gray-200';
  }
};
