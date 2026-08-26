import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuditTrailTab } from '@/components/tasks/AuditTrailTab';
import { apiService } from '@/services/api';
import {
  QueuedTaskStatus,
  type RunsAuditsResponse,
  type StatusAuditDto,
  type StatusAuditsResponse,
} from '@/types/task.types';

vi.mock('@/services/api', () => ({
  apiService: { getStatusAudit: vi.fn(), getRunsAudit: vi.fn() },
}));

const getStatusAudit = vi.mocked(apiService.getStatusAudit);
const getRunsAudit = vi.mocked(apiService.getRunsAudit);

type StatusResult = Awaited<ReturnType<typeof apiService.getStatusAudit>>;
type RunsResult = Awaited<ReturnType<typeof apiService.getRunsAudit>>;

const respondStatus = (page: StatusAuditsResponse) => ({ data: page }) as unknown as StatusResult;
const respondRuns = (page: RunsAuditsResponse) => ({ data: page }) as unknown as RunsResult;

const taskId = '11111111-1111-1111-1111-111111111111';
const Total = 250;
const PageSize = 100;

/** One page of a trail of {@link Total} transitions, newest first, as the endpoint answers it. */
function pageOf(skip: number, take: number): StatusAuditsResponse {
  const audits: StatusAuditDto[] = [];

  for (let i = skip; i < Math.min(skip + take, Total); i++) {
    audits.push({
      // Newest first, so the highest audit id comes first.
      id: Total - i,
      queuedTaskId: taskId,
      updatedAtUtc: new Date(Date.UTC(2026, 7, 26) - i * 60_000).toISOString(),
      newStatus: QueuedTaskStatus.Completed,
    });
  }

  return { audits, totalCount: Total, skip, take };
}

function renderTab(trail: 'status' | 'runs' = 'status', id: string = taskId) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  const ui = (forId: string) => (
    <QueryClientProvider client={client}>
      <AuditTrailTab taskId={forId} trail={trail} />
    </QueryClientProvider>
  );

  const view = render(ui(id));
  return { ...view, showTask: (other: string) => view.rerender(ui(other)) };
}

describe('AuditTrailTab paging', () => {
  beforeEach(() => {
    getStatusAudit.mockReset();
    getRunsAudit.mockReset();

    getStatusAudit.mockImplementation(async (_id, skip = 0, take = PageSize) =>
      respondStatus(pageOf(skip, take)));
    getRunsAudit.mockImplementation(async (_id, skip = 0, take = PageSize) =>
      respondRuns({ audits: [], totalCount: 0, skip, take }));
  });

  it('asks the server for one page and reports the whole trail', async () => {
    renderTab();

    expect(await screen.findByText('Showing 1 - 100 of 250')).toBeInTheDocument();
    expect(getStatusAudit).toHaveBeenCalledWith(taskId, 0, PageSize);
  });

  it('reaches the transitions past the first page instead of stopping at it', async () => {
    const user = userEvent.setup();
    renderTab();

    await screen.findByText('Showing 1 - 100 of 250');
    await user.click(screen.getByRole('button', { name: 'Next' }));

    await waitFor(() => expect(screen.getByText('Showing 101 - 200 of 250')).toBeInTheDocument());
    expect(getStatusAudit).toHaveBeenCalledWith(taskId, PageSize, PageSize);
  });

  it('offers the way back, and refuses to go before the first page or past the last', async () => {
    const user = userEvent.setup();
    renderTab();

    await screen.findByText('Showing 1 - 100 of 250');
    expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled();

    await user.click(screen.getByRole('button', { name: 'Next' }));
    await waitFor(() => expect(screen.getByText('Showing 101 - 200 of 250')).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: 'Next' }));
    await waitFor(() => expect(screen.getByText('Showing 201 - 250 of 250')).toBeInTheDocument());
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();

    await user.click(screen.getByRole('button', { name: 'Previous' }));
    await waitFor(() => expect(screen.getByText('Showing 101 - 200 of 250')).toBeInTheDocument());
  });

  it('opens a different task at its own first page', async () => {
    const user = userEvent.setup();
    const { showTask } = renderTab();

    await screen.findByText('Showing 1 - 100 of 250');
    await user.click(screen.getByRole('button', { name: 'Next' }));
    await waitFor(() => expect(screen.getByText('Showing 101 - 200 of 250')).toBeInTheDocument());

    showTask('22222222-2222-2222-2222-222222222222');

    await waitFor(() => expect(screen.getByText('Showing 1 - 100 of 250')).toBeInTheDocument());
  });

  it('says a task has no history only on the FIRST page', async () => {
    getStatusAudit.mockImplementation(async (_id, skip = 0, take = PageSize) =>
      respondStatus({ audits: [], totalCount: 0, skip, take }));

    renderTab();

    expect(await screen.findByText('No status history available')).toBeInTheDocument();
  });

  it('reads the runs trail from its own endpoint', async () => {
    getRunsAudit.mockImplementation(async (_id, skip = 0, take = PageSize) =>
      respondRuns({
        audits: [
          {
            id: 2,
            queuedTaskId: taskId,
            executedAt: '2026-08-26T10:00:00Z',
            executionTimeMs: 22,
            status: QueuedTaskStatus.Completed,
          },
          {
            id: 1,
            queuedTaskId: taskId,
            executedAt: '2026-08-26T09:00:00Z',
            executionTimeMs: 11,
            status: QueuedTaskStatus.Completed,
          },
        ],
        totalCount: 2,
        skip,
        take,
      }));

    renderTab('runs');

    await waitFor(() => expect(getRunsAudit).toHaveBeenCalledWith(taskId, 0, PageSize));
    expect(getStatusAudit).not.toHaveBeenCalled();
  });
});
