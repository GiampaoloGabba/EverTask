import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { LateBadge, MisfireBadge } from '@/components/tasks/OccurrenceBadges';
import { QueuedTaskStatus } from '@/types/task.types';

const slot = '2026-08-26T02:00:00Z';
const at = (secondsAfterSlot: number) =>
  new Date(Date.parse(slot) + secondsAfterSlot * 1000).toISOString();

describe('MisfireBadge', () => {
  it('says a row stands for a replayed slot, with the size of the backlog behind it', () => {
    render(<MisfireBadge kind="CatchUp" missedCount={12} missedCountIsExact={true} />);

    expect(screen.getByText('Catch-up (12)')).toBeInTheDocument();
  });

  it('marks a count that stopped at its bound as a lower bound instead of a total', () => {
    render(<MisfireBadge kind="CatchUp" missedCount={251} missedCountIsExact={false} />);

    expect(screen.getByText('Catch-up (≥251)')).toBeInTheDocument();
  });

  it('names the whole run of slots a FireOnce row collapsed', () => {
    render(<MisfireBadge kind="FireOnce" missedCount={4} missedCountIsExact={true} />);

    expect(screen.getByText('Fire once (4)')).toBeInTheDocument();
  });

  it('shows nothing for an occurrence that is simply its own slot', () => {
    const { container } = render(<MisfireBadge kind="None" />);
    expect(container).toBeEmptyDOMElement();

    // `Late` is not a decision that created the row: it is measured at delivery, and the LateBadge says it.
    const late = render(<MisfireBadge kind="Late" />);
    expect(late.container).toBeEmptyDOMElement();
  });

  it('shows nothing when the API omitted the kind altogether', () => {
    const { container } = render(<MisfireBadge kind={undefined} missedCount={undefined} />);
    expect(container).toBeEmptyDOMElement();
  });
});

describe('LateBadge', () => {
  it('measures lateness from when the run STARTED, not from when it ended', () => {
    // A punctual delivery with a slow handler: started on its slot, ended three minutes later. Measuring
    // against the end would report the execution time as tardiness.
    render(
      <LateBadge slotUtc={slot} startedAtUtc={at(0.2)} status={QueuedTaskStatus.Completed} />
    );

    expect(screen.queryByText(/late/)).not.toBeInTheDocument();
  });

  it('reports how far past its slot a run began, in the coarsest unit that reads true', () => {
    render(<LateBadge slotUtc={slot} startedAtUtc={at(45 * 60)} status={QueuedTaskStatus.Completed} />);

    expect(screen.getByText('45m late')).toBeInTheDocument();
  });

  it('measures a row that has not started but still can against the clock', () => {
    const longAgo = new Date(Date.now() - 2 * 60 * 60 * 1000).toISOString();

    render(<LateBadge slotUtc={longAgo} startedAtUtc={null} status={QueuedTaskStatus.Queued} />);

    expect(screen.getByText('2h late')).toBeInTheDocument();
  });

  it('reports nothing for a row that ended without ever starting', () => {
    // A cancelled occurrence never runs, so measuring it against the clock shows a number that grows for ever.
    const longAgo = new Date(Date.now() - 5 * 60 * 60 * 1000).toISOString();

    const { container } = render(
      <LateBadge slotUtc={longAgo} startedAtUtc={null} status={QueuedTaskStatus.Cancelled} />
    );

    expect(container).toBeEmptyDOMElement();
  });

  it('reports nothing when the API omitted the slot or the start', () => {
    const noSlot = render(
      <LateBadge slotUtc={undefined} startedAtUtc={at(3600)} status={QueuedTaskStatus.Completed} />
    );
    expect(noSlot.container).toBeEmptyDOMElement();

    const noStart = render(
      <LateBadge slotUtc={slot} startedAtUtc={undefined} status={QueuedTaskStatus.Completed} />
    );
    expect(noStart.container).toBeEmptyDOMElement();
  });
});
