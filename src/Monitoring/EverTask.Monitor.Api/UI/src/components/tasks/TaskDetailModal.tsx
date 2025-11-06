import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { TaskStatusBadge } from '@/components/common/TaskStatusBadge';
import { JsonViewer } from '@/components/common/JsonViewer';
import { Timeline } from '@/components/common/Timeline';
import { ExecutionLogsTab } from '@/components/tasks/ExecutionLogsTab';
import { TaskDetailDto, AuditLevel } from '@/types/task.types';
import { format } from 'date-fns';
import { Copy, AlertCircle, RefreshCw, Calendar, Clock } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { useState } from 'react';
import { Alert, AlertDescription } from '@/components/ui/alert';

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
          variant: 'default' as const,
          description: 'Complete audit trail with all status and execution history'
        };
      case AuditLevel.Minimal:
        return {
          label: 'Minimal',
          variant: 'secondary' as const,
          description: 'Minimal audit trail - errors only + last execution timestamp'
        };
      case AuditLevel.ErrorsOnly:
        return {
          label: 'Errors Only',
          variant: 'secondary' as const,
          description: 'Only failed executions are audited'
        };
      case AuditLevel.None:
        return {
          label: 'None',
          variant: 'outline' as const,
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
  }));

  return (
    <div className="space-y-6">
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
            <TaskStatusBadge status={task.status} />
          </div>
        </CardHeader>
        <CardContent>
          <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
            <div>
              <span className="text-sm text-muted-foreground">Created At</span>
              <p className="text-sm font-medium">{formatDate(task.createdAtUtc)}</p>
            </div>
            <div>
              <span className="text-sm text-muted-foreground">Last Execution</span>
              <p className="text-sm font-medium">{formatDate(task.lastExecutionUtc)}</p>
            </div>
            <div>
              <span className="text-sm text-muted-foreground">Scheduled Execution</span>
              <p className="text-sm font-medium">{formatDate(task.scheduledExecutionUtc)}</p>
            </div>
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
            <div className="space-y-4">
              <div>
                <span className="text-sm text-muted-foreground">Type</span>
                <p className="text-sm font-medium break-all">{task.type}</p>
              </div>
              <div>
                <span className="text-sm text-muted-foreground">Handler</span>
                <p className="text-sm font-medium" title={handlerInfo.fullName}>
                  {handlerInfo.shortName}
                </p>
                <p className="text-xs text-muted-foreground break-all mt-1">
                  {handlerInfo.fullName}
                </p>
              </div>
              <div>
                <span className="text-sm text-muted-foreground">Queue Name</span>
                <p className="text-sm font-medium">{task.queueName || 'Default Queue'}</p>
              </div>
              {task.taskKey && (
                <div>
                  <span className="text-sm text-muted-foreground">Task Key</span>
                  <p className="text-sm font-medium break-all">{task.taskKey}</p>
                </div>
              )}
            </div>

            <div className="space-y-4">
              {task.auditLevel !== null && task.auditLevel !== undefined && (
                <div>
                  <span className="text-sm text-muted-foreground">Audit Level</span>
                  <div className="mt-1">
                    {(() => {
                      const auditInfo = getAuditLevelInfo(task.auditLevel);
                      return auditInfo ? (
                        <div>
                          <Badge variant={auditInfo.variant}>{auditInfo.label}</Badge>
                          <p className="text-xs text-muted-foreground mt-1">
                            {auditInfo.description}
                          </p>
                        </div>
                      ) : null;
                    })()}
                  </div>
                </div>
              )}

              <div>
                <span className="text-sm text-muted-foreground">Is Recurring</span>
                <div className="mt-1">
                  {task.isRecurring ? (
                    <Badge variant="outline" className="bg-purple-50 text-purple-700 border-purple-200">
                      <RefreshCw className="h-3 w-3 mr-1" />
                      Recurring Task
                    </Badge>
                  ) : (
                    <Badge variant="outline">Standard Task</Badge>
                  )}
                </div>
              </div>

              {task.isRecurring && task.recurringInfo && (
                <div>
                  <span className="text-sm text-muted-foreground">Recurring Schedule</span>
                  <p className="text-sm font-medium">{task.recurringInfo}</p>
                </div>
              )}

              {task.isRecurring && task.currentRunCount !== null && (
                <div>
                  <span className="text-sm text-muted-foreground">Run Progress</span>
                  <p className="text-sm font-medium">
                    {task.currentRunCount} {task.maxRuns ? `/ ${task.maxRuns}` : ''} runs
                  </p>
                </div>
              )}

              {task.isRecurring && task.runUntil && (
                <div>
                  <span className="text-sm text-muted-foreground">Runs Until</span>
                  <div className="flex items-center gap-1 text-sm font-medium mt-1">
                    <Calendar className="h-3 w-3" />
                    {formatDate(task.runUntil)}
                  </div>
                </div>
              )}

              {task.isRecurring && task.nextRunUtc && (
                <div>
                  <span className="text-sm text-muted-foreground">Next Run</span>
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

      {/* Request Parameters */}
      <Card>
        <CardHeader>
          <CardTitle>Request Parameters</CardTitle>
        </CardHeader>
        <CardContent>
          <JsonViewer jsonString={task.request} />
        </CardContent>
      </Card>

      {/* Exception (if present) */}
      {task.exception && (
        <Alert variant="destructive">
          <AlertCircle className="h-4 w-4" />
          <AlertDescription>
            <div className="space-y-2">
              <p className="font-medium">Task Exception</p>
              <pre className="text-xs whitespace-pre-wrap break-words mt-2">
                {task.exception}
              </pre>
            </div>
          </AlertDescription>
        </Alert>
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
