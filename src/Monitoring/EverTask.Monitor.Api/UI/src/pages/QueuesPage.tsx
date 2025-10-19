import { useQueues } from '@/hooks/useQueues';
import { QueueCard } from '@/components/queues/QueueCard';
import { QueueComparisonChart } from '@/components/queues/QueueComparisonChart';
import { LoadingSpinner } from '@/components/common/LoadingSpinner';
import { EmptyState } from '@/components/common/EmptyState';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Layers, AlertCircle } from 'lucide-react';

export function QueuesPage() {
  const { data: queues, isLoading, isError } = useQueues();

  if (isLoading) {
    return <LoadingSpinner text="Loading queues..." />;
  }

  if (isError) {
    return (
      <Alert variant="destructive">
        <AlertCircle className="h-4 w-4" />
        <AlertDescription>
          Failed to load queue data. Please try refreshing the page.
        </AlertDescription>
      </Alert>
    );
  }

  if (!queues || queues.length === 0) {
    return (
      <div className="space-y-6">
        <div>
          <h1 className="text-3xl font-bold tracking-tight">Queues</h1>
          <p className="text-muted-foreground">
            View metrics and performance for all task queues
          </p>
        </div>
        <EmptyState
          icon={Layers}
          title="No queues found"
          description="There are no active queues at the moment. Queues will appear here once tasks are dispatched."
        />
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-3xl font-bold tracking-tight">Queues</h1>
        <p className="text-muted-foreground">
          View metrics and performance for all task queues
        </p>
      </div>

      {/* Queue Cards */}
      <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
        {queues.map((queue) => (
          <QueueCard key={queue.queueName || 'default'} queue={queue} />
        ))}
      </div>

      {/* Comparison Chart */}
      {queues.length > 1 && (
        <QueueComparisonChart queues={queues} />
      )}
    </div>
  );
}
