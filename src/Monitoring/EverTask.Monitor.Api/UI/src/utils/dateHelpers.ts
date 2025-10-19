import { format, formatDistanceToNow, parseISO } from 'date-fns';

export const formatDate = (dateString: string | null): string => {
  if (!dateString) return 'N/A';
  try {
    return format(parseISO(dateString), 'PPpp');
  } catch {
    return dateString;
  }
};

export const formatDateShort = (dateString: string | null): string => {
  if (!dateString) return 'N/A';
  try {
    return format(parseISO(dateString), 'PP');
  } catch {
    return dateString;
  }
};

export const formatTimeAgo = (dateString: string | null): string => {
  if (!dateString) return 'N/A';
  try {
    return formatDistanceToNow(parseISO(dateString), { addSuffix: true });
  } catch {
    return dateString;
  }
};

export const formatDuration = (milliseconds: number): string => {
  if (milliseconds < 1000) {
    return `${milliseconds}ms`;
  } else if (milliseconds < 60000) {
    return `${(milliseconds / 1000).toFixed(2)}s`;
  } else if (milliseconds < 3600000) {
    return `${(milliseconds / 60000).toFixed(2)}min`;
  } else {
    return `${(milliseconds / 3600000).toFixed(2)}h`;
  }
};
