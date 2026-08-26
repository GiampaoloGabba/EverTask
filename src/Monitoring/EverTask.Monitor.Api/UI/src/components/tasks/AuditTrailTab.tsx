import { useEffect, useState } from 'react';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { Timeline } from '@/components/common/Timeline';
import { useRunsAudits, useStatusAudits } from '@/hooks/useTasks';

interface AuditTrailTabProps {
  taskId: string;
  /** Which of the two trails of the detail this tab shows. */
  trail: 'status' | 'runs';
}

/** How many entries one page of the tab holds. The endpoint takes the same number as its `take`. */
const PageSize = 100;

/**
 * One of the two audit trails of the task detail, a page at a time.
 *
 * The trails are paged by the SERVER (`skip` / `take` + the total), because a long-lived recurring row
 * records one transition per state per run: opening the detail of an old schedule used to transfer its whole
 * history to show the first twenty rows of it. The detail's own `statusAudits` / `runsAudits` blocks carry
 * only the first page for the same reason, so this tab asks the endpoint rather than reading them — that is
 * also what lets it move past page one.
 */
export function AuditTrailTab({ taskId, trail }: AuditTrailTabProps) {
  const [skip, setSkip] = useState(0);

  // A different task is a different history: staying on page four would open on rows it does not have.
  useEffect(() => setSkip(0), [taskId, trail]);

  const status = useStatusAudits(taskId, skip, PageSize, { enabled: trail === 'status' && !!taskId });
  const runs = useRunsAudits(taskId, skip, PageSize, { enabled: trail === 'runs' && !!taskId });

  const query = trail === 'status' ? status : runs;
  const { data, isLoading } = query;

  const items =
    trail === 'status'
      ? (status.data?.audits ?? []).map((audit) => ({
          id: audit.id,
          timestamp: audit.updatedAtUtc,
          status: audit.newStatus,
          exception: audit.exception,
        }))
      : // The runs timeline has always read oldest-first WITHIN what it shows; the page itself is
        // newest-first, like the endpoint and like the status trail.
        (runs.data?.audits ?? [])
          .map((audit) => ({
            id: audit.id,
            timestamp: audit.executedAt,
            status: audit.status,
            exception: audit.exception,
            executionTimeMs: audit.executionTimeMs,
          }))
          .reverse();

  if (isLoading) {
    return (
      <div className="space-y-3">
        {[...Array(3)].map((_, i) => (
          <Skeleton key={i} className="h-12 w-full" />
        ))}
      </div>
    );
  }

  const totalCount = data?.totalCount ?? 0;
  const pageCount = data?.audits.length ?? 0;

  // Only the FIRST page can say the task has no history at all: an empty page further in is a trail that
  // shrank under the reader (audit cleanup), and it still needs its way back.
  if (pageCount === 0 && skip === 0) {
    return (
      <p className="text-sm text-muted-foreground text-center py-8">
        {trail === 'status' ? 'No status history available' : 'No runs history available'}
      </p>
    );
  }

  const first = pageCount === 0 ? 0 : skip + 1;
  const last = skip + pageCount;
  const hasMore = last < totalCount;

  return (
    <div className="space-y-3">
      <Timeline items={items} />
      {(hasMore || skip > 0) && (
        <div className="flex items-center justify-between text-sm text-muted-foreground">
          <span>
            Showing {first} - {last} of {totalCount}
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
