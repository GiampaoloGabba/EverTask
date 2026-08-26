import { Badge } from '@/components/ui/badge';
import { History, Layers, Timer } from 'lucide-react';
import { QueuedTaskStatus, type MisfireKind } from '@/types/task.types';

/**
 * How late a delivery is against the slot it stands for, in a form a badge can show.
 * Anything under a second is on time as far as an operator is concerned.
 */
function latenessLabel(slotUtc: string | null | undefined, startedAtUtc: string | null | undefined,
                       waiting: boolean): string | null {
  if (!slotUtc) return null;

  // Only two instants can be compared against the slot: when the delivery really started (`startedAtUtc`,
  // which the API derives from the run itself) and, while it has not started and still can, now. A row that
  // ended without ever starting — a cancelled occurrence — has no lateness at all, and measuring it against
  // the clock would show a number that grows for ever.
  if (!startedAtUtc && !waiting) return null;

  const slot = new Date(slotUtc).getTime();
  const started = startedAtUtc ? new Date(startedAtUtc).getTime() : Date.now();
  const lateMs = started - slot;

  if (!Number.isFinite(lateMs) || lateMs < 1000) return null;

  const seconds = Math.round(lateMs / 1000);
  if (seconds < 60) return `${seconds}s late`;

  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes}m late`;

  const hours = Math.round(minutes / 60);
  if (hours < 48) return `${hours}h late`;

  return `${Math.round(hours / 24)}d late`;
}

interface MisfireBadgeProps {
  kind: MisfireKind | null | undefined;
  missedCount?: number | null;
  missedCountIsExact?: boolean | null;
}

/**
 * What the row was created out of: a replayed slot, or a whole run of missed slots collapsed into one.
 * Nothing is shown for an occurrence that is simply its own slot.
 */
export function MisfireBadge({ kind, missedCount, missedCountIsExact }: MisfireBadgeProps) {
  if (kind !== 'CatchUp' && kind !== 'FireOnce') return null;

  const count =
    missedCount && missedCount > 0
      ? ` (${missedCountIsExact === false ? '≥' : ''}${missedCount})`
      : '';

  if (kind === 'CatchUp') {
    return (
      <Badge
        variant="outline"
        className="text-xs bg-orange-50 text-orange-700 border-orange-200"
        title="Replayed slot: this occurrence is one of a backlog being caught up"
      >
        <History className="h-3 w-3 mr-1" />
        Catch-up{count}
      </Badge>
    );
  }

  return (
    <Badge
      variant="outline"
      className="text-xs bg-orange-50 text-orange-700 border-orange-200"
      title="A whole run of missed slots collapsed into this single occurrence"
    >
      <Layers className="h-3 w-3 mr-1" />
      Fire once{count}
    </Badge>
  );
}

interface LateBadgeProps {
  slotUtc: string | null | undefined;
  /**
   * When the delivery started, as the API reports it. Never `lastExecutionUtc`: that column is written on
   * terminal transitions, so it says when the run ENDED and would report a slow handler as tardiness.
   */
  startedAtUtc: string | null | undefined;
  status: QueuedTaskStatus;
}

/** How far past its nominal slot the delivery started — or, while it has not started yet, how far past it is now. */
export function LateBadge({ slotUtc, startedAtUtc, status }: LateBadgeProps) {
  const waiting =
    status === QueuedTaskStatus.WaitingQueue ||
    status === QueuedTaskStatus.Queued ||
    status === QueuedTaskStatus.Pending ||
    status === QueuedTaskStatus.ServiceStopped;

  const label = latenessLabel(slotUtc, startedAtUtc, waiting);
  if (!label) return null;

  return (
    <Badge
      variant="outline"
      className="text-xs bg-yellow-50 text-yellow-700 border-yellow-200"
      title={startedAtUtc ? 'Started later than its nominal slot' : 'Its nominal slot is already past'}
    >
      <Timer className="h-3 w-3 mr-1" />
      {label}
    </Badge>
  );
}
