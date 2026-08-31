import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { TaskStatusBadge } from '@/components/common/TaskStatusBadge';
import { JsonViewer } from '@/components/common/JsonViewer';
import { ExceptionViewer } from '@/components/common/ExceptionViewer';
import { ExecutionLogsTab } from '@/components/tasks/ExecutionLogsTab';
import { OccurrencesTab } from '@/components/tasks/OccurrencesTab';
import { AuditTrailTab } from '@/components/tasks/AuditTrailTab';
import { LateBadge, MisfireBadge } from '@/components/tasks/OccurrenceBadges';
import { TaskDetailDto, AuditLevel } from '@/types/task.types';
import { format } from 'date-fns';
import { Copy, RefreshCw, Calendar, Clock, Timer, CalendarClock, Hourglass, Layers, Globe, OctagonX } from 'lucide-react';
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert';
import { Link } from 'react-router-dom';
import { Button } from '@/components/ui/button';
import { Breadcrumb } from '@/components/common/Breadcrumb';
import { useState } from 'react';

interface TaskDetailModalProps {
  task: TaskDetailDto;
}

export function TaskDetailModal({ task }: TaskDetailModalProps) {
  const [copiedId, setCopiedId] = useState(false);

  const formatDate = (dateStr: string | null | undefined) => {
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

  const breadcrumbItems = [
    { label: 'Tasks', path: '/tasks' },
    { label: handlerInfo.shortName },
  ];

  // A durable SCHEDULE row is the one that owns occurrences; an occurrence carries a parent instead.
  const isOccurrence = task.parentTaskId !== null && task.parentTaskId !== undefined;
  const isDurableSchedule = task.occurrenceMode === 'Durable' && !isOccurrence;

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
              {isDurableSchedule && (
                <Badge variant="outline" className="bg-indigo-50 text-indigo-700 border-indigo-200">
                  <Layers className="h-3 w-3 mr-1" />
                  Durable occurrences
                </Badge>
              )}
              {isOccurrence && (
                <Badge variant="outline" className="bg-indigo-50 text-indigo-700 border-indigo-200">
                  <Layers className="h-3 w-3 mr-1" />
                  Occurrence
                </Badge>
              )}
              {task.occurrence && (
                <MisfireBadge
                  kind={task.occurrence.misfireKind}
                  missedCount={task.occurrence.missedCount}
                  missedCountIsExact={task.occurrence.missedCountIsExact}
                />
              )}
              {isOccurrence && (
                <LateBadge
                  slotUtc={task.nominalSlotUtc}
                  startedAtUtc={task.startedAtUtc}
                  status={task.status}
                />
              )}
              {task.timeZoneId && (
                <Badge variant="outline" className="bg-sky-50 text-sky-700 border-sky-200">
                  <Globe className="h-3 w-3 mr-1" />
                  {task.timeZoneId}
                </Badge>
              )}
              <Badge variant="outline" className="bg-gray-50 text-gray-700 border-gray-300">
                Queue: {task.queueName || 'Default'}
              </Badge>
              {task.throttledUntil && (
                <Badge variant="outline" className="bg-amber-50 text-amber-700 border-amber-200">
                  <Hourglass className="h-3 w-3 mr-1" />
                  Throttled
                </Badge>
              )}
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

      {/* Catch-up halt (a durable schedule that stopped itself) */}
      {task.halt && (
        <Alert variant="destructive">
          <OctagonX className="h-4 w-4" />
          <AlertTitle>Catch-up halted</AlertTitle>
          <AlertDescription>
            {task.halt.reason ?? 'The backlog exceeded the configured cap'}
            {'. '}
            {task.halt.isExact ? '' : 'At least '}
            {task.halt.detectedAtLeast} slot{task.halt.detectedAtLeast === 1 ? ' was' : 's were'} due at cursor{' '}
            {formatDate(task.halt.cursorUtc)}, decided {formatDate(task.halt.atUtc)} against schedule version{' '}
            {task.halt.scheduleVersion}. A halt does not release itself: call{' '}
            <code>ResumeSchedule</code> or <code>Reschedule</code> to let the series continue.
          </AlertDescription>
        </Alert>
      )}

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

              {task.throttledUntil && (
                <div>
                  <span className="text-xs uppercase tracking-wide text-gray-600 font-medium">Throttled Until</span>
                  <div className="flex items-center gap-1 text-sm font-medium text-amber-700 mt-1">
                    <Hourglass className="h-3 w-3" />
                    {formatDate(task.throttledUntil)}
                  </div>
                  <p className="text-xs text-muted-foreground mt-1">
                    Reserved rate-limit slot (in-memory, single-node view)
                  </p>
                </div>
              )}

              {isOccurrence && (
                <div>
                  <span className="text-xs uppercase tracking-wide text-gray-600 font-medium">Schedule</span>
                  <p className="text-sm font-medium mt-1">
                    <Link className="text-blue-600 hover:underline break-all" to={`/tasks/${task.parentTaskId}`}>
                      {task.parentTaskId}
                    </Link>
                  </p>
                  {task.occurrence?.runNumber && (
                    <p className="text-xs text-muted-foreground mt-1">Run {task.occurrence.runNumber} of the series</p>
                  )}
                </div>
              )}

              {task.nominalSlotUtc && (
                <div>
                  <span className="text-xs uppercase tracking-wide text-gray-600 font-medium">Nominal Slot</span>
                  <div className="flex items-center gap-1 text-sm font-medium mt-1">
                    <CalendarClock className="h-3 w-3" />
                    {formatDate(task.nominalSlotUtc)}
                  </div>
                  <p className="text-xs text-muted-foreground mt-1">
                    The slot this occurrence stands for, not the moment it was fired
                  </p>
                </div>
              )}

              {task.occurrence?.misfireKind && task.occurrence.missedFromUtc && (
                <div>
                  <span className="text-xs uppercase tracking-wide text-gray-600 font-medium">Missed Work</span>
                  <p className="text-sm font-medium mt-1">
                    {formatDate(task.occurrence.missedFromUtc)} &rarr; {formatDate(task.occurrence.missedThroughUtc)}
                  </p>
                  {task.occurrence.missedCount !== null && (
                    <p className="text-xs text-muted-foreground mt-1">
                      {task.occurrence.missedCountIsExact === false ? 'At least ' : ''}
                      {task.occurrence.missedCount} slot{task.occurrence.missedCount === 1 ? '' : 's'} in that range
                    </p>
                  )}
                </div>
              )}

              {task.isRecurring && task.occurrenceMode && (
                <div>
                  <span className="text-xs uppercase tracking-wide text-gray-600 font-medium">Occurrence Mode</span>
                  <p className="text-sm font-medium mt-1">
                    {task.occurrenceMode === 'Durable'
                      ? 'Durable — every due slot becomes its own row'
                      : 'Inline — the schedule row runs the handler itself'}
                  </p>
                </div>
              )}

              {task.isRecurring && task.misfirePolicy && (
                <div>
                  <span className="text-xs uppercase tracking-wide text-gray-600 font-medium">On Misfire</span>
                  <p className="text-sm font-medium mt-1">{task.misfirePolicy}</p>
                </div>
              )}

              {task.timeZoneId && (
                <div>
                  <span className="text-xs uppercase tracking-wide text-gray-600 font-medium">Time Zone</span>
                  <p className="text-sm font-medium mt-1">{task.timeZoneId}</p>
                </div>
              )}

              {(task.scheduleVersion ?? 0) > 0 && (
                <div>
                  <span className="text-xs uppercase tracking-wide text-gray-600 font-medium">Schedule Version</span>
                  <p className="text-sm font-medium mt-1">{task.scheduleVersion}</p>
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
            <TabsList className={isDurableSchedule ? 'grid w-full grid-cols-4' : 'grid w-full grid-cols-3'}>
              <TabsTrigger value="status">
                Status History ({task.statusAuditsTotalCount})
              </TabsTrigger>
              <TabsTrigger value="runs">
                Runs History ({task.runsAuditsTotalCount})
              </TabsTrigger>
              <TabsTrigger value="logs">
                Execution Logs
              </TabsTrigger>
              {isDurableSchedule && (
                <TabsTrigger value="occurrences">
                  Occurrences
                </TabsTrigger>
              )}
            </TabsList>
            <TabsContent value="status" className="mt-4">
              <AuditTrailTab taskId={task.id} trail="status" />
            </TabsContent>
            <TabsContent value="runs" className="mt-4">
              <AuditTrailTab taskId={task.id} trail="runs" />
            </TabsContent>
            <TabsContent value="logs" className="mt-4">
              <ExecutionLogsTab taskId={task.id} />
            </TabsContent>
            {isDurableSchedule && (
              <TabsContent value="occurrences" className="mt-4">
                <OccurrencesTab scheduleId={task.id} />
              </TabsContent>
            )}
          </Tabs>
        </CardContent>
      </Card>
    </div>
  );
}
