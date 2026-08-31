import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { TaskStatusBadge } from '@/components/common/TaskStatusBadge';
import { EmptyState } from '@/components/common/EmptyState';
import { LateBadge, MisfireBadge } from '@/components/tasks/OccurrenceBadges';
import { useOccurrences } from '@/hooks/useTasks';
import { format } from 'date-fns';
import { CalendarX } from 'lucide-react';

interface OccurrencesTabProps {
  scheduleId: string;
}

/** How many occurrences one page of the tab holds. The endpoint takes the same number as its `take`. */
const PageSize = 100;

/**
 * The rows a durable schedule materialized, newest slot first. Each of them is a task in its own right,
 * so the row navigates to its own detail page.
 */
export function OccurrencesTab({ scheduleId }: OccurrencesTabProps) {
  const navigate = useNavigate();
  const [skip, setSkip] = useState(0);

  // A different schedule is a different series: staying on page four of the previous one would open on rows
  // it does not have.
  useEffect(() => setSkip(0), [scheduleId]);

  const { data, isLoading } = useOccurrences(scheduleId, true, skip, PageSize);

  const formatDate = (dateStr: string | null | undefined) => {
    if (!dateStr) return '-';
    try {
      return format(new Date(dateStr), 'MMM d, yyyy HH:mm:ss');
    } catch {
      return '-';
    }
  };

  if (isLoading) {
    return (
      <div className="space-y-3">
        {[...Array(3)].map((_, i) => (
          <Skeleton key={i} className="h-12 w-full" />
        ))}
      </div>
    );
  }

  // Only the FIRST page can say the schedule has no occurrence at all: an empty page further in is a series
  // that shrank under the reader (retention, a cancel), and it still needs its way back.
  if (!data || (data.occurrences.length === 0 && skip === 0)) {
    return (
      <EmptyState
        icon={CalendarX}
        title="No occurrences yet"
        description="This schedule has not materialized any occurrence. Inline schedules never do: they run the handler from the schedule row itself."
      />
    );
  }

  const first = data.occurrences.length === 0 ? 0 : skip + 1;
  const last = skip + data.occurrences.length;
  const hasMore = last < data.totalCount;

  return (
    <div className="space-y-3">
      <p className="text-sm text-muted-foreground">
        {data.totalCount} occurrence{data.totalCount === 1 ? '' : 's'}, newest slot first
      </p>
      <div className="rounded-md border bg-white">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Status</TableHead>
              <TableHead>Slot</TableHead>
              <TableHead>Run</TableHead>
              <TableHead>Executed</TableHead>
              <TableHead>Info</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {data.occurrences.map((occurrence) => (
              <TableRow
                key={occurrence.id}
                className="cursor-pointer hover:bg-gray-50"
                onClick={() => navigate(`/tasks/${occurrence.id}`)}
              >
                <TableCell>
                  <TaskStatusBadge status={occurrence.status} />
                </TableCell>
                <TableCell>
                  <span className="text-sm font-mono">{formatDate(occurrence.occurrence.slotUtc)}</span>
                  {occurrence.occurrence.timeZoneId && (
                    <p className="text-xs text-muted-foreground">{occurrence.occurrence.timeZoneId}</p>
                  )}
                </TableCell>
                <TableCell>
                  <span className="text-sm">{occurrence.occurrence.runNumber ?? '-'}</span>
                </TableCell>
                <TableCell>
                  <span className="text-sm">{formatDate(occurrence.lastExecutionUtc)}</span>
                </TableCell>
                <TableCell>
                  <div className="flex gap-1 flex-wrap">
                    <MisfireBadge
                      kind={occurrence.occurrence.misfireKind}
                      missedCount={occurrence.occurrence.missedCount}
                      missedCountIsExact={occurrence.occurrence.missedCountIsExact}
                    />
                    <LateBadge
                      slotUtc={occurrence.occurrence.slotUtc}
                      startedAtUtc={occurrence.startedAtUtc}
                      status={occurrence.status}
                    />
                  </div>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </div>
      {(hasMore || skip > 0) && (
        <div className="flex items-center justify-between text-sm text-muted-foreground">
          <span>
            Showing {first} - {last} of {data.totalCount}
          </span>
          <div className="flex gap-2">
            <Button
              variant="outline"
              size="sm"
              onClick={() => setSkip(Math.max(0, skip - PageSize))}
              disabled={skip === 0 || isLoading}
            >
              Previous
            </Button>
            <Button
              variant="outline"
              size="sm"
              onClick={() => setSkip(skip + PageSize)}
              disabled={!hasMore || isLoading}
            >
              Next
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}
