import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { TaskStatusBadge } from '@/components/common/TaskStatusBadge';
import { JsonViewer } from '@/components/common/JsonViewer';
import { Timeline } from '@/components/common/Timeline';
import { ExceptionViewer } from '@/components/common/ExceptionViewer';
import { ExecutionLogsTab } from '@/components/tasks/ExecutionLogsTab';
import { TaskDetailDto, AuditLevel } from '@/types/task.types';
import { format } from 'date-fns';
import { Copy, RefreshCw, Calendar, Clock, Timer, CalendarClock } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Breadcrumb } from '@/components/common/Breadcrumb';
import { useState } from 'react';

interface TaskDetailModalProps {
  task: TaskDetailDto;
}

export function TaskDetailModal({ task }: TaskDetailModalProps) {
  const [copiedId, setCopiedId] = useState(false);

  const formatDate = (dateStr: string | null) => {
    if (!dateStr) return '-';
    try {
      return format(new Date(dateStr), 'MMM d, yyyy HH:mm:ss');
    } catch {
      return '-';
    }
  };

  const formatExecutionTime = (ms: number) => {
    if (ms === 0) return '-';
    if (ms < 1000) return `${ms.toFixed(0)}ms`;
    return `${(ms / 1000).toFixed(2)}s`;
  };

  const formatHandler = (handler: string) => {
    // Extract short name
    const parts = handler.split(',')[0].split('.');
    const shortName = parts[parts.length - 1];
    return { shortName, fullName: handler };
  };

  const handleCopyId = () => {
    navigator.clipboard.writeText(task.id);
    setCopiedId(true);
    setTimeout(() => setCopiedId(false), 2000);
  };

  const getAuditLevelInfo = (level: number | null) => {
    if (level === null || level === undefined) return null;

    switch (level) {
      case AuditLevel.Full:
        return {
          label: 'Full',
          className: 'border-green-300 text-green-700 bg-green-50',
          description: 'Complete audit trail with all status and execution history'
        };
      case AuditLevel.Minimal:
        return {
          label: 'Minimal',
          className: 'border-yellow-300 text-yellow-700 bg-yellow-50',
          description: 'Minimal audit trail - errors only + last execution timestamp'
        };
      case AuditLevel.ErrorsOnly:
        return {
          label: 'Errors Only',
          className: 'border-red-300 text-red-700 bg-red-50',
          description: 'Only failed executions are audited'
        };
      case AuditLevel.None:
        return {
          label: 'None',
          className: 'border-gray-300 text-gray-700 bg-gray-50',
          description: 'No audit trail - task data only'
        };
      default:
        return null;
    }
  };

  const handlerInfo = formatHandler(task.handler);
  const statusAudits = task.statusAudits.map(audit => ({
    id: audit.id,
    timestamp: audit.updatedAtUtc,
    status: audit.newStatus,
    exception: audit.exception,
  }));

  const runsAudits = task.runsAudits.map(audit => ({
    id: audit.id,
    timestamp: audit.executedAt,
    status: audit.status,
    exception: audit.exception,
    executionTimeMs: audit.executionTimeMs,
  }));

  const breadcrumbItems = [
    { label: 'Tasks', path: '/tasks' },
    { label: handlerInfo.shortName },
  ];

  return (
    <div className="space-y-6">
      {/* Breadcrumb */}
      <Breadcrumb items={breadcrumbItems} />

      {/* Header */}
      <Card>
        <CardHeader>
          <div className="flex items-start justify-between">
            <div className="space-y-2">
              <CardTitle>Task Details</CardTitle>
              <div className="flex items-center gap-2">
                <code className="text-xs bg-gray-100 px-2 py-1 rounded font-mono">
                  {task.id}
                </code>
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={handleCopyId}
                  className="h-6 w-6 p-0"
                >
                  <Copy className={copiedId ? 'h-3 w-3 text-green-600' : 'h-3 w-3'} />
                </Button>
              </div>
            </div>
            <div className="flex items-center gap-2 flex-wrap justify-end">
              {task.isRecurring && (
                <Badge variant="outline" className="bg-purple-50 text-purple-700 border-purple-200">
                  <RefreshCw className="h-3 w-3 mr-1" />
                  Recurring
                </Badge>
              )}
              <Badge variant="outline" className="bg-gray-50 text-gray-700 border-gray-300">
                Queue: {task.queueName || 'Default'}
              </Badge>
              <TaskStatusBadge status={task.status} />
            </div>
          </div>
        </CardHeader>
        <CardContent>
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4">
            <div>
              <span className="text-xs uppercase tracking-wide text-gray-600 font-medium">Created At</span>
              <div className="flex items-center gap-1 text-sm font-medium mt-1">
                <Calendar className="h-3 w-3 text-gray-600" />
                {formatDate(task.createdAtUtc)}
              </div>
            </div>
            <div>
              <span className="text-xs uppercase tracking-wide text-gray-600 font-medium">Last Execution</span>
              <div className="flex items-center gap-1 text-sm font-medium mt-1">
                <Clock className="h-3 w-3 text-blue-600" />
                {formatDate(task.lastExecutionUtc)}
              </div>
            </div>
            <div>
              <span className="text-xs uppercase tracking-wide text-gray-600 font-medium">Duration</span>
              <div className="flex items-center gap-1 text-sm font-medium font-mono mt-1">
                <Timer className="h-3 w-3 text-green-600" />
                {formatExecutionTime(task.executionTimeMs)}
              </div>
            </div>
            {/* Quarta colonna dinamica */}
            {!task.lastExecutionUtc && task.scheduledExecutionUtc ? (
              // Task mai eseguito → mostra quando è schedulato
              <div>
                <span className="text-xs uppercase tracking-wide text-gray-600 font-medium">Scheduled For</span>
                <div className="flex items-center gap-1 text-sm font-medium mt-1">
                  <CalendarClock className="h-3 w-3 text-purple-600" />
                  {formatDate(task.scheduledExecutionUtc)}
                </div>
              </div>
            ) : task.isRecurring && task.nextRunUtc ? (
              // Recurring già avviato → mostra prossimo run
              <div>
                <span className="text-xs uppercase tracking-wide text-gray-600 font-medium">Next Run</span>
                <div className="flex items-center gap-1 text-sm font-medium mt-1">
                  <Clock className="h-3 w-3 text-purple-600" />
                  {formatDate(task.nextRunUtc)}
                </div>
              </div>
            ) : null}
          </div>
        </CardContent>
      </Card>

      {/* Task Information */}
      <Card>
        <CardHeader>
          <CardTitle>Task Information</CardTitle>
        </CardHeader>
        <CardContent>
          <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
            {/* Colonna sinistra: Handler + Request Parameters */}
            <div className="space-y-4">
              <div>
                <span className="text-xs uppercase tracking-wide text-gray-600 font-medium">Handler</span>
                <p className="text-sm font-medium mt-1" title={handlerInfo.fullName}>
                  {handlerInfo.shortName}
                </p>
                <p className="text-xs text-muted-foreground break-all mt-1">
                  {handlerInfo.fullName}
                </p>
              </div>

              <div>
                <span className="text-xs uppercase tracking-wide text-gray-600 font-medium">Request Parameters</span>
                <div className="mt-2">
                  <JsonViewer jsonString={task.request} />
                </div>
              </div>
            </div>

            {/* Colonna destra: altri dettagli */}
            <div className="space-y-4">
              {task.auditLevel !== null && task.auditLevel !== undefined && (
                <div>
                  <span className="text-xs uppercase tracking-wide text-gray-600 font-medium">Audit Level</span>
                  <div className="mt-1">
                    {(() => {
                      const auditInfo = getAuditLevelInfo(task.auditLevel);
                      return auditInfo ? (
                        <div>
                          <Badge variant="outline" className={auditInfo.className}>{auditInfo.label}</Badge>
                          <p className="text-xs text-muted-foreground mt-1">
                            {auditInfo.description}
                          </p>
                        </div>
                      ) : null;
                    })()}
                  </div>
                </div>
              )}

              {task.taskKey && (
                <div>
                  <span className="text-xs uppercase tracking-wide text-gray-600 font-medium">Task Key</span>
                  <p className="text-sm font-medium break-all mt-1">{task.taskKey}</p>
                </div>
              )}

              {task.scheduledExecutionUtc && (
                <div>
                  <span className="text-xs uppercase tracking-wide text-gray-600 font-medium">Scheduled Execution</span>
                  <div className="flex items-center gap-1 text-sm font-medium mt-1">
                    <CalendarClock className="h-3 w-3" />
                    {formatDate(task.scheduledExecutionUtc)}
                  </div>
                </div>
              )}

              {task.isRecurring && task.recurringInfo && (
                <div>
                  <span className="text-xs uppercase tracking-wide text-gray-600 font-medium">Recurring Schedule</span>
                  <p className="text-sm font-medium mt-1">{task.recurringInfo}</p>
                </div>
              )}

              {task.isRecurring && task.currentRunCount !== null && (
                <div>
                  <span className="text-xs uppercase tracking-wide text-gray-600 font-medium">Run Progress</span>
                  <p className="text-sm font-medium mt-1">
                    {task.currentRunCount} {task.maxRuns ? `/ ${task.maxRuns}` : ''} runs
                  </p>
                </div>
              )}

              {task.isRecurring && task.runUntil && (
                <div>
                  <span className="text-xs uppercase tracking-wide text-gray-600 font-medium">Runs Until</span>
                  <div className="flex items-center gap-1 text-sm font-medium mt-1">
                    <Calendar className="h-3 w-3" />
                    {formatDate(task.runUntil)}
                  </div>
                </div>
              )}

              {task.isRecurring && task.nextRunUtc && (
                <div>
                  <span className="text-xs uppercase tracking-wide text-gray-600 font-medium">Next Run</span>
                  <div className="flex items-center gap-1 text-sm font-medium mt-1">
                    <Clock className="h-3 w-3 text-purple-600" />
                    {formatDate(task.nextRunUtc)}
                  </div>
                </div>
              )}
            </div>
          </div>
        </CardContent>
      </Card>

      {/* Exception (if present) */}
      {task.exception && (
        <ExceptionViewer exception={task.exception} previewLines={3} variant="alert" />
      )}

      {/* Tabs for History */}
      <Card>
        <CardHeader>
          <CardTitle>History & Logs</CardTitle>
        </CardHeader>
        <CardContent>
          <Tabs defaultValue="status" className="w-full">
            <TabsList className="grid w-full grid-cols-3">
              <TabsTrigger value="status">
                Status History ({statusAudits.length})
              </TabsTrigger>
              <TabsTrigger value="runs">
                Runs History ({runsAudits.length})
              </TabsTrigger>
              <TabsTrigger value="logs">
                Execution Logs
              </TabsTrigger>
            </TabsList>
            <TabsContent value="status" className="mt-4">
              {statusAudits.length > 0 ? (
                <Timeline items={statusAudits} />
              ) : (
                <p className="text-sm text-muted-foreground text-center py-8">
                  No status history available
                </p>
              )}
            </TabsContent>
            <TabsContent value="runs" className="mt-4">
              {runsAudits.length > 0 ? (
                <Timeline items={[...runsAudits].reverse()} />
              ) : (
                <p className="text-sm text-muted-foreground text-center py-8">
                  No runs history available
                </p>
              )}
            </TabsContent>
            <TabsContent value="logs" className="mt-4">
              <ExecutionLogsTab taskId={task.id} />
            </TabsContent>
          </Tabs>
        </CardContent>
      </Card>
    </div>
  );
}
