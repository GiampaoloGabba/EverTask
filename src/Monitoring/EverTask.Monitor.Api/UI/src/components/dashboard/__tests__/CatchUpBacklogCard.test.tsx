import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { CatchUpBacklogCard } from '@/components/dashboard/CatchUpBacklogCard';
import type { CatchUpBacklogDto } from '@/types/dashboard.types';

const backlog = (overrides: Partial<CatchUpBacklogDto> = {}): CatchUpBacklogDto => ({
  pending: 0,
  active: 0,
  failed: 0,
  skipped: 0,
  completed: 0,
  lagSeconds: 0,
  haltedSchedules: 0,
  ...overrides,
});

describe('CatchUpBacklogCard', () => {
  it('shows one count per row state', () => {
    render(
      <CatchUpBacklogCard
        backlog={backlog({ pending: 7, active: 2, failed: 1, skipped: 3, completed: 41 })}
      />
    );

    for (const label of ['Pending', 'Active', 'Failed', 'Skipped', 'Completed'])
      expect(screen.getByText(label)).toBeInTheDocument();

    expect(screen.getByText('7')).toBeInTheDocument();
    expect(screen.getByText('41')).toBeInTheDocument();
  });

  it('reports a standing halt, which is the one condition an operator has to act on', () => {
    render(<CatchUpBacklogCard backlog={backlog({ pending: 5, haltedSchedules: 2 })} />);

    expect(screen.getByText('2 halted schedules')).toBeInTheDocument();
  });

  it('is shown for a halt even when no occurrence exists at all', () => {
    // A halted schedule materializes nothing by definition, so a card hidden on "no rows" would hide the
    // very state that produced none.
    render(<CatchUpBacklogCard backlog={backlog({ haltedSchedules: 1 })} />);

    expect(screen.getByText('1 halted schedule')).toBeInTheDocument();
  });

  it('says nothing at all when there is no durable schedule in the store', () => {
    const { container } = render(<CatchUpBacklogCard backlog={backlog()} />);

    expect(container).toBeEmptyDOMElement();
  });

  it('reports how far behind the oldest pending slot is', () => {
    render(
      <CatchUpBacklogCard
        backlog={backlog({
          pending: 3,
          oldestPendingSlotUtc: '2026-08-26T02:00:00Z',
          lagSeconds: 2700,
        })}
      />
    );

    expect(screen.getByText(/Oldest pending slot:/)).toBeInTheDocument();
    expect(screen.getByText(/45m behind/)).toBeInTheDocument();
  });

  it('does not claim a lag for a slot that is still in the future', () => {
    render(
      <CatchUpBacklogCard
        backlog={backlog({ pending: 1, oldestPendingSlotUtc: '2026-08-26T02:00:00Z', lagSeconds: 0 })}
      />
    );

    expect(screen.queryByText(/behind/)).not.toBeInTheDocument();
  });
});
