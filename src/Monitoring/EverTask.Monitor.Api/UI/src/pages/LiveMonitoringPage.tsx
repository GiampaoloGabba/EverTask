import { useState } from 'react';
import { useRealtimeStore } from '@/stores/realtimeStore';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { ScrollArea } from '@/components/ui/scroll-area';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { EmptyState } from '@/components/common/EmptyState';
import { EverTaskEventData } from '@/types/signalr.types';
import { formatDistanceToNow } from 'date-fns';
import { Pause, Play, Trash2, Activity, ChevronDown, ChevronRight } from 'lucide-react';
import { getSeverityColor } from '@/utils/statusHelpers';
import { cn } from '@/lib/utils';

export function LiveMonitoringPage() {
  const { events, isPaused, togglePause, clearEvents } = useRealtimeStore();
  const [severityFilter, setSeverityFilter] = useState<string>('all');
  const [expandedEvents, setExpandedEvents] = useState<Set<string>>(new Set());

  const filteredEvents = events.filter((event) => {
    if (severityFilter === 'all') return true;
    return event.severity === severityFilter;
  });

  const toggleEventExpansion = (eventId: string) => {
    setExpandedEvents((prev) => {
      const next = new Set(prev);
      if (next.has(eventId)) {
        next.delete(eventId);
      } else {
        next.add(eventId);
      }
      return next;
    });
  };

  const formatHandler = (handler: string) => {
    const parts = handler.split('.');
    return parts[parts.length - 1] || handler;
  };

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-3xl font-bold tracking-tight">Live Monitoring</h1>
        <p className="text-muted-foreground">
          Real-time task execution events
        </p>
      </div>

      {/* Controls */}
      <Card>
        <CardContent className="pt-6">
          <div className="flex items-center justify-between flex-wrap gap-4">
            <div className="flex items-center gap-2">
              <Button
                variant={isPaused ? 'default' : 'outline'}
                size="sm"
                onClick={togglePause}
              >
                {isPaused ? (
                  <>
                    <Play className="h-4 w-4 mr-2" />
                    Resume
                  </>
                ) : (
                  <>
                    <Pause className="h-4 w-4 mr-2" />
                    Pause
                  </>
                )}
              </Button>
              <Button
                variant="outline"
                size="sm"
                onClick={clearEvents}
                disabled={events.length === 0}
              >
                <Trash2 className="h-4 w-4 mr-2" />
                Clear Events
              </Button>
            </div>

            <div className="flex items-center gap-2">
              <span className="text-sm text-muted-foreground">Filter by severity:</span>
              <Select value={severityFilter} onValueChange={setSeverityFilter}>
                <SelectTrigger className="w-[180px]">
                  <SelectValue placeholder="All severities" />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="all">All severities</SelectItem>
                  <SelectItem value="Information">Information</SelectItem>
                  <SelectItem value="Warning">Warning</SelectItem>
                  <SelectItem value="Error">Error</SelectItem>
                </SelectContent>
              </Select>
            </div>
          </div>

          {isPaused && (
            <div className="mt-4 p-3 bg-yellow-50 border border-yellow-200 rounded-md">
              <p className="text-sm text-yellow-800">
                Event stream is paused. Click Resume to continue receiving events.
              </p>
            </div>
          )}
        </CardContent>
      </Card>

      {/* Event Stream */}
      {filteredEvents.length === 0 ? (
        <EmptyState
          icon={Activity}
          title="No events"
          description={
            severityFilter === 'all'
              ? 'No events received yet. Events will appear here as tasks are executed.'
              : `No ${severityFilter} events. Try changing the severity filter.`
          }
        />
      ) : (
        <Card>
          <CardHeader>
            <CardTitle>Event Stream ({filteredEvents.length})</CardTitle>
          </CardHeader>
          <CardContent>
            <ScrollArea className="h-[600px] pr-4">
              <div className="space-y-3">
                {filteredEvents.map((event: EverTaskEventData) => {
                  const isExpanded = expandedEvents.has(event.taskId + event.eventDateUtc);
                  const hasException = event.exception && event.exception.trim() !== '';

                  return (
                    <Card
                      key={event.taskId + event.eventDateUtc}
                      className={cn('border', getSeverityColor(event.severity))}
                    >
                      <CardContent className="pt-4">
                        <div className="space-y-2">
                          <div className="flex items-start justify-between gap-2">
                            <div className="flex-1 space-y-1">
                              <div className="flex items-center gap-2 flex-wrap">
                                <Badge variant="outline" className={getSeverityColor(event.severity)}>
                                  {event.severity}
                                </Badge>
                                <span className="text-xs text-muted-foreground">
                                  {formatDistanceToNow(new Date(event.eventDateUtc), {
                                    addSuffix: true,
                                  })}
                                </span>
                              </div>
                              <p className="text-sm font-medium">{formatHandler(event.taskType)}</p>
                              <p className="text-sm text-muted-foreground">{event.message}</p>
                            </div>
                          </div>

                          {(hasException || event.taskParameters) && (
                            <button
                              onClick={() =>
                                toggleEventExpansion(event.taskId + event.eventDateUtc)
                              }
                              className="flex items-center gap-1 text-sm text-blue-600 hover:text-blue-700 transition-colors mt-2"
                            >
                              {isExpanded ? (
                                <ChevronDown className="h-4 w-4" />
                              ) : (
                                <ChevronRight className="h-4 w-4" />
                              )}
                              <span>Show details</span>
                            </button>
                          )}

                          {isExpanded && (
                            <div className="mt-3 space-y-3 pt-3 border-t">
                              <div>
                                <span className="text-xs font-medium text-muted-foreground">
                                  Task ID:
                                </span>
                                <code className="block text-xs bg-gray-100 p-2 rounded mt-1 break-all">
                                  {event.taskId}
                                </code>
                              </div>

                              {event.taskParameters && (
                                <div>
                                  <span className="text-xs font-medium text-muted-foreground">
                                    Parameters:
                                  </span>
                                  <pre className="text-xs bg-gray-100 p-2 rounded mt-1 overflow-auto max-h-[200px]">
                                    {JSON.stringify(JSON.parse(event.taskParameters), null, 2)}
                                  </pre>
                                </div>
                              )}

                              {hasException && (
                                <div>
                                  <span className="text-xs font-medium text-red-600">
                                    Exception:
                                  </span>
                                  <pre className="text-xs bg-red-50 p-2 rounded mt-1 overflow-auto max-h-[300px] text-red-800 whitespace-pre-wrap break-words">
                                    {event.exception}
                                  </pre>
                                </div>
                              )}
                            </div>
                          )}
                        </div>
                      </CardContent>
                    </Card>
                  );
                })}
              </div>
            </ScrollArea>
          </CardContent>
        </Card>
      )}
    </div>
  );
}
