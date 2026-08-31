import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { OccurrencesTab } from '@/components/tasks/OccurrencesTab';
import { apiService } from '@/services/api';
import { QueuedTaskStatus, type OccurrenceDto, type OccurrencesResponse } from '@/types/task.types';

vi.mock('@/services/api', () => ({
  apiService: { getOccurrences: vi.fn() },
}));

const getOccurrences = vi.mocked(apiService.getOccurrences);

type OccurrencesResult = Awaited<ReturnType<typeof apiService.getOccurrences>>;
const respond = (page: OccurrencesResponse) => ({ data: page }) as unknown as OccurrencesResult;

const scheduleId = '11111111-1111-1111-1111-111111111111';
const Total = 300;
const PageSize = 100;

/** One page of a series of {@link Total} occurrences, newest slot first, as the endpoint answers it. */
function pageOf(skip: number, take: number): OccurrencesResponse {
  const occurrences: OccurrenceDto[] = [];

  for (let i = skip; i < Math.min(skip + take, Total); i++) {
    occurrences.push({
      id: `occurrence-${i}`,
      parentTaskId: scheduleId,
      status: QueuedTaskStatus.Completed,
      occurrence: {
        slotUtc: new Date(Date.UTC(2026, 7, 26) - i * 60_000).toISOString(),
        // Newest slot first, so the newest run number comes first too.
        runNumber: Total - i,
      },
      createdAtUtc: '2026-08-26T00:00:00Z',
      executionTimeMs: 12,
      scheduleVersion: 0,
    });
  }

  return { occurrences, totalCount: Total, skip, take };
}

function renderTab(id: string = scheduleId) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  const ui = (forId: string) => (
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <OccurrencesTab scheduleId={forId} />
      </MemoryRouter>
    </QueryClientProvider>
  );

  const view = render(ui(id));
  return { ...view, showSchedule: (other: string) => view.rerender(ui(other)) };
}

describe('OccurrencesTab paging', () => {
  beforeEach(() => {
    getOccurrences.mockReset();
    getOccurrences.mockImplementation(async (_id, skip = 0, take = PageSize) =>
      respond(pageOf(skip, take)));
  });

  it('asks the server for one page and reports the whole series', async () => {
    renderTab();

    expect(await screen.findByText('300 occurrences, newest slot first')).toBeInTheDocument();
    expect(screen.getByText('Showing 1 - 100 of 300')).toBeInTheDocument();
    expect(getOccurrences).toHaveBeenCalledWith(scheduleId, 0, PageSize);
  });

  it('reaches the rows past the first page instead of stopping at it', async () => {
    // A failed occurrence at row 150 is unreachable without these controls, which is why they exist.
    const user = userEvent.setup();
    renderTab();

    await screen.findByText('Showing 1 - 100 of 300');
    await user.click(screen.getByRole('button', { name: 'Next' }));

    await waitFor(() => expect(screen.getByText('Showing 101 - 200 of 300')).toBeInTheDocument());
    expect(getOccurrences).toHaveBeenCalledWith(scheduleId, PageSize, PageSize);

    // The second page really carries the second hundred: run 200 is its newest row.
    expect(screen.getByText('200')).toBeInTheDocument();
  });

  it('offers the way back, and refuses to go before the first page or past the last', async () => {
    const user = userEvent.setup();
    renderTab();

    await screen.findByText('Showing 1 - 100 of 300');
    expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled();

    await user.click(screen.getByRole('button', { name: 'Next' }));
    await waitFor(() => expect(screen.getByText('Showing 101 - 200 of 300')).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: 'Next' }));
    await waitFor(() => expect(screen.getByText('Showing 201 - 300 of 300')).toBeInTheDocument());
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();

    await user.click(screen.getByRole('button', { name: 'Previous' }));
    await waitFor(() => expect(screen.getByText('Showing 101 - 200 of 300')).toBeInTheDocument());
  });

  it('opens a different schedule at its own first page', async () => {
    const user = userEvent.setup();
    const { showSchedule } = renderTab();

    await screen.findByText('Showing 1 - 100 of 300');
    await user.click(screen.getByRole('button', { name: 'Next' }));
    await waitFor(() => expect(screen.getByText('Showing 101 - 200 of 300')).toBeInTheDocument());

    showSchedule('22222222-2222-2222-2222-222222222222');

    await waitFor(() => expect(screen.getByText('Showing 1 - 100 of 300')).toBeInTheDocument());
  });

  it('says a schedule has no occurrence only on the FIRST page', async () => {
    getOccurrences.mockImplementation(async (_id, skip = 0, take = PageSize) =>
      respond({ occurrences: [], totalCount: 0, skip, take }));

    renderTab();

    expect(await screen.findByText('No occurrences yet')).toBeInTheDocument();
  });

  it('keeps the way back when a page further in has emptied under the reader', async () => {
    // Retention or a cancel can shrink a series while it is open: an empty page there is not "no occurrences".
    const user = userEvent.setup();
    renderTab();

    await screen.findByText('Showing 1 - 100 of 300');

    getOccurrences.mockImplementation(async (_id, skip = 0, take = PageSize) =>
      respond({ occurrences: [], totalCount: Total, skip, take }));

    await user.click(screen.getByRole('button', { name: 'Next' }));

    await waitFor(() => expect(screen.getByRole('button', { name: 'Previous' })).toBeEnabled());
    expect(screen.queryByText('No occurrences yet')).not.toBeInTheDocument();
  });
});
