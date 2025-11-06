import { useState, useEffect, useRef } from 'react';
import { apiService } from '@/services/api';
import type { ExecutionLogDto } from '@/types/task.types';
import { Button } from '@/components/ui/button';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { LoadingSpinner } from '@/components/common/LoadingSpinner';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { AlertCircle, Download, ChevronsDown, ChevronsUp } from 'lucide-react';
import { format } from 'date-fns';

interface ExecutionLogsTabProps {
  taskId: string;
}

export function ExecutionLogsTab({ taskId }: ExecutionLogsTabProps) {
  const [logs, setLogs] = useState<ExecutionLogDto[]>([]);
  const [totalCount, setTotalCount] = useState(0);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [levelFilter, setLevelFilter] = useState<string>('all');
  const [skip, setSkip] = useState(0);
  const [autoScroll, setAutoScroll] = useState(true);
  const logsEndRef = useRef<HTMLDivElement>(null);
  const take = 100;

  useEffect(() => {
    fetchLogs();
  }, [taskId, levelFilter, skip]);

  useEffect(() => {
    if (autoScroll && logs.length > 0) {
      logsEndRef.current?.scrollIntoView({ behavior: 'smooth' });
    }
  }, [logs, autoScroll]);

  const fetchLogs = async () => {
    try {
      setIsLoading(true);
      setError(null);
      const response = await apiService.getExecutionLogs(
        taskId,
        skip,
        take,
        levelFilter === 'all' ? undefined : levelFilter
      );
      setLogs(response.data.logs);
      setTotalCount(response.data.totalCount);
    } catch (err: any) {
      setError(err.message || 'Failed to load execution logs');
    } finally {
      setIsLoading(false);
    }
  };

  const handleExportJSON = () => {
    const dataStr = JSON.stringify(logs, null, 2);
    const blob = new Blob([dataStr], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `task-${taskId}-logs.json`;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    URL.revokeObjectURL(url);
  };

  const handleExportTXT = () => {
    const txtData = logs
      .map((log) => {
        const timestamp = format(new Date(log.timestampUtc), 'yyyy-MM-dd HH:mm:ss.SSS');
        let line = `[${timestamp}] [${log.level.toUpperCase().padEnd(11)}] ${log.message}`;
        if (log.exceptionDetails) {
          line += `\n${log.exceptionDetails}\n`;
        }
        return line;
      })
      .join('\n');
    const blob = new Blob([txtData], { type: 'text/plain' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `task-${taskId}-logs.txt`;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    URL.revokeObjectURL(url);
  };

  const getLevelColor = (level: string): string => {
    switch (level.toLowerCase()) {
      case 'trace':
        return 'text-gray-500';
      case 'debug':
        return 'text-cyan-400';
      case 'information':
        return 'text-blue-400';
      case 'warning':
        return 'text-yellow-400';
      case 'error':
        return 'text-red-400';
      case 'critical':
        return 'text-red-600 font-bold';
      default:
        return 'text-gray-100';
    }
  };

  const hasMore = skip + logs.length < totalCount;
  const hasNoLogs = totalCount === 0 && !isLoading && !error;
  const isFiltered = levelFilter !== 'all';

  return (
    <div className="space-y-4">
      {/* Controls */}
      <div className="flex items-center justify-between gap-4 flex-wrap">
        <div className="flex items-center gap-2">
          <span className="text-sm text-muted-foreground">Filter by level:</span>
          <Select value={levelFilter} onValueChange={(value) => {
            setLevelFilter(value);
            setSkip(0); // Reset pagination
          }}>
            <SelectTrigger className="w-[150px]">
              <SelectValue placeholder="All levels" />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">All levels</SelectItem>
              <SelectItem value="Trace">Trace</SelectItem>
              <SelectItem value="Debug">Debug</SelectItem>
              <SelectItem value="Information">Information</SelectItem>
              <SelectItem value="Warning">Warning</SelectItem>
              <SelectItem value="Error">Error</SelectItem>
              <SelectItem value="Critical">Critical</SelectItem>
            </SelectContent>
          </Select>
        </div>

        <div className="flex items-center gap-2">
          <Button
            variant="outline"
            size="sm"
            onClick={() => setAutoScroll(!autoScroll)}
            title={autoScroll ? 'Disable auto-scroll' : 'Enable auto-scroll'}
          >
            {autoScroll ? (
              <ChevronsDown className="h-4 w-4 mr-1" />
            ) : (
              <ChevronsUp className="h-4 w-4 mr-1" />
            )}
            Auto-scroll
          </Button>
          <Button variant="outline" size="sm" onClick={handleExportJSON}>
            <Download className="h-4 w-4 mr-1" />
            Export JSON
          </Button>
          <Button variant="outline" size="sm" onClick={handleExportTXT}>
            <Download className="h-4 w-4 mr-1" />
            Export TXT
          </Button>
        </div>
      </div>

      {/* Content Area */}
      {error ? (
        <Alert variant="destructive">
          <AlertCircle className="h-4 w-4" />
          <AlertDescription>{error}</AlertDescription>
        </Alert>
      ) : isLoading && logs.length === 0 ? (
        <div className="flex items-center justify-center py-12">
          <LoadingSpinner />
        </div>
      ) : hasNoLogs ? (
        <div className="text-center py-12">
          <p className="text-sm text-muted-foreground">
            {isFiltered
              ? `No logs found for level "${levelFilter}".`
              : 'No logs captured for this task.'}
          </p>
          <p className="text-xs text-muted-foreground mt-2">
            {isFiltered
              ? 'Try selecting a different log level or "All levels".'
              : 'Log capture may be disabled or no logs were written during execution.'}
          </p>
        </div>
      ) : (
        <>
          {/* Terminal Display */}
          <div className="bg-gray-900 rounded-lg p-4 font-mono text-sm overflow-x-auto max-h-[600px] overflow-y-auto relative">
            {isLoading && (
              <div className="absolute top-2 right-2 bg-gray-800 rounded px-2 py-1 text-xs text-gray-400 flex items-center gap-1">
                <LoadingSpinner className="w-3 h-3" />
                Loading...
              </div>
            )}
            {logs.map((log, index) => (
              <LogEntry key={`${log.id}-${index}`} log={log} getLevelColor={getLevelColor} />
            ))}
            <div ref={logsEndRef} />
          </div>

          {/* Pagination Info and Controls */}
          <div className="flex items-center justify-between text-sm text-muted-foreground">
            <span>
              Showing {skip + 1} - {Math.min(skip + logs.length, totalCount)} of {totalCount} logs
            </span>
            <div className="flex gap-2">
              {skip > 0 && (
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => setSkip(Math.max(0, skip - take))}
                  disabled={isLoading}
                >
                  Previous
                </Button>
              )}
              {hasMore && (
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => setSkip(skip + take)}
                  disabled={isLoading}
                >
                  Load More
                </Button>
              )}
            </div>
          </div>
        </>
      )}
    </div>
  );
}

interface LogEntryProps {
  log: ExecutionLogDto;
  getLevelColor: (level: string) => string;
}

function LogEntry({ log, getLevelColor }: LogEntryProps) {
  const [showException, setShowException] = useState(false);
  const timestamp = format(new Date(log.timestampUtc), 'HH:mm:ss.SSS');
  const levelColor = getLevelColor(log.level);

  return (
    <div className="mb-1">
      <div className="flex gap-2">
        <span className="text-gray-500 text-xs">{timestamp}</span>
        <span className={`${levelColor} min-w-[90px]`}>
          [{log.level.toUpperCase().padEnd(11)}]
        </span>
        <span className="text-gray-100 flex-1">{log.message}</span>
      </div>
      {log.exceptionDetails && (
        <div className="mt-1 ml-24">
          <button
            onClick={() => setShowException(!showException)}
            className="text-xs text-red-400 hover:text-red-300 underline"
          >
            {showException ? 'Hide' : 'Show'} Exception
          </button>
          {showException && (
            <pre className="text-xs text-red-300 mt-1 whitespace-pre-wrap break-words">
              {log.exceptionDetails}
            </pre>
          )}
        </div>
      )}
    </div>
  );
}
