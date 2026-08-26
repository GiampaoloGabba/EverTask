import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { formatNumber } from '@/utils/formatters';
import type { CatchUpBacklogDto } from '@/types/dashboard.types';
import { format } from 'date-fns';
import { OctagonX } from 'lucide-react';

interface CatchUpBacklogCardProps {
  backlog: CatchUpBacklogDto;
}

/** How far past its slot the oldest occupied slot already is, in the coarsest unit that still reads true. */
function formatLag(seconds: number): string {
  if (seconds < 60) return `${Math.round(seconds)}s`;
  if (seconds < 3600) return `${Math.round(seconds / 60)}m`;
  if (seconds < 172800) return `${Math.round(seconds / 3600)}h`;
  return `${Math.round(seconds / 86400)}d`;
}

const states: Array<{ key: keyof CatchUpBacklogDto; label: string; className: string; title: string }> = [
  {
    key: 'pending',
    label: 'Pending',
    className: 'text-blue-700',
    title: 'Materialized and waiting to start',
  },
  {
    key: 'active',
    label: 'Active',
    className: 'text-purple-700',
    title: 'Running right now',
  },
  {
    key: 'failed',
    label: 'Failed',
    className: 'text-red-700',
    title: 'Ended Failed, after their retries',
  },
  {
    key: 'skipped',
    label: 'Skipped',
    className: 'text-gray-600',
    title: 'Cancelled on their own or with their schedule: they will never run',
  },
  {
    key: 'completed',
    label: 'Completed',
    className: 'text-green-700',
    title: 'Ran to completion and still in the store',
  },
];

/**
 * The occurrences of every durable schedule, by row state. Slots a schedule DROPPED never became rows and are
 * therefore absent here: they are reported when they happen, by the OccurrenceSkipped monitoring event.
 */
export function CatchUpBacklogCard({ backlog }: CatchUpBacklogCardProps) {
  const total =
    backlog.pending + backlog.active + backlog.failed + backlog.skipped + backlog.completed;

  if (total === 0 && backlog.haltedSchedules === 0) return null;

  return (
    <Card>
      <CardHeader>
        <div className="flex items-start justify-between">
          <div>
            <CardTitle>Durable Occurrences</CardTitle>
            <CardDescription>
              Every slot a durable schedule materialized, by state
            </CardDescription>
          </div>
          {backlog.haltedSchedules > 0 && (
            <Badge variant="outline" className="bg-red-50 text-red-700 border-red-200">
              <OctagonX className="h-3 w-3 mr-1" />
              {backlog.haltedSchedules} halted schedule{backlog.haltedSchedules === 1 ? '' : 's'}
            </Badge>
          )}
        </div>
      </CardHeader>
      <CardContent>
        <div className="grid grid-cols-2 gap-4 md:grid-cols-5">
          {states.map((state) => (
            <div key={state.key} title={state.title}>
              <p className="text-xs uppercase tracking-wide text-gray-600 font-medium">{state.label}</p>
              <p className={`text-2xl font-bold ${state.className}`}>
                {formatNumber(backlog[state.key] as number)}
              </p>
            </div>
          ))}
        </div>

        {backlog.oldestPendingSlotUtc && (
          <p className="text-xs text-muted-foreground mt-4">
            Oldest pending slot: {format(new Date(backlog.oldestPendingSlotUtc), 'MMM d, yyyy HH:mm:ss')}
            {backlog.lagSeconds > 0 && ` — ${formatLag(backlog.lagSeconds)} behind`}
          </p>
        )}
      </CardContent>
    </Card>
  );
}
