import { useRateLimits } from '@/hooks/useRateLimits';
import { KPICard } from '@/components/dashboard/KPICard';
import { LoadingSpinner } from '@/components/common/LoadingSpinner';
import { EmptyState } from '@/components/common/EmptyState';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { formatNumber } from '@/utils/formatters';
import { format, formatDistanceToNow } from 'date-fns';
import { Gauge, Hourglass, KeyRound, ShieldAlert, AlertCircle, Info } from 'lucide-react';

export function RateLimitsPage() {
  const { data, isLoading, isError } = useRateLimits();

  if (isLoading) {
    return <LoadingSpinner text="Loading rate limits..." />;
  }

  if (isError || !data) {
    return (
      <Alert variant="destructive">
        <AlertCircle className="h-4 w-4" />
        <AlertDescription>
          Failed to load rate-limit data. Please try refreshing the page.
        </AlertDescription>
      </Alert>
    );
  }

  const formatSlot = (slot: string) => {
    try {
      return `${format(new Date(slot), 'MMM d, yyyy HH:mm:ss')} (${formatDistanceToNow(new Date(slot), { addSuffix: true })})`;
    } catch {
      return slot;
    }
  };

  const Header = () => (
    <div>
      <h1 className="text-3xl font-bold tracking-tight">Rate Limits</h1>
      <p className="text-muted-foreground">
        Keyed rate limiter state: per-key parked tasks and their reserved slots
      </p>
    </div>
  );

  // No limiter introspection available (e.g. standalone API mode, or EverTask not
  // registered in the same container).
  if (!data.enabled) {
    return (
      <div className="space-y-6">
        <Header />
        <EmptyState
          icon={Gauge}
          title="Rate limiter not available"
          description="No keyed rate limiter is registered in this process, or the API runs in standalone mode without access to the limiter. Declare a RateLimitPolicy on a handler to start throttling tasks."
        />
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <Header />

      {/* Summary KPIs */}
      <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-4">
        <KPICard
          icon={Hourglass}
          title="Throttled Tasks"
          value={formatNumber(data.throttledTasks)}
          subtitle={`Parking lot: ${formatNumber(data.throttledTasks)} / ${formatNumber(data.maxParkedTasks)}`}
        />
        <KPICard
          icon={KeyRound}
          title="Tracked Keys"
          value={formatNumber(data.trackedKeys)}
          subtitle="Active (task type, key) buckets"
        />
        <KPICard
          icon={ShieldAlert}
          title="Fail-Open Count"
          value={formatNumber(data.failOpenCount)}
          subtitle="Acquisitions that bypassed the limiter"
        />
        <KPICard
          icon={Gauge}
          title="Active Buckets"
          value={formatNumber(data.keys.length)}
          subtitle="Buckets with parked tasks"
        />
      </div>

      {/* Single-node disclaimer */}
      <Alert>
        <Info className="h-4 w-4" />
        <AlertDescription>
          This view is in-memory and <strong>single-node</strong>: it reflects this process'
          limiter and scheduler only. Across multiple instances, each enforces its own budget.
        </AlertDescription>
      </Alert>

      {/* Buckets table */}
      <Card>
        <CardHeader>
          <CardTitle>Parked Buckets</CardTitle>
        </CardHeader>
        <CardContent>
          {data.keys.length === 0 ? (
            <EmptyState
              icon={Hourglass}
              title="No tasks throttled"
              description="No tasks are currently parked waiting for rate-limit budget."
            />
          ) : (
            <div className="rounded-md border">
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Queue</TableHead>
                    <TableHead>Key</TableHead>
                    <TableHead className="text-right">Parked</TableHead>
                    <TableHead>Next Slot</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {data.keys.map((bucket) => (
                    <TableRow key={`${bucket.queueName}::${bucket.key}`}>
                      <TableCell>
                        <span className="text-sm text-muted-foreground">
                          {bucket.queueName || 'Default'}
                        </span>
                      </TableCell>
                      <TableCell>
                        <code className="text-xs font-mono break-all" title={bucket.key}>
                          {bucket.key}
                        </code>
                      </TableCell>
                      <TableCell className="text-right">
                        <Badge variant="outline" className="bg-amber-50 text-amber-700 border-amber-200">
                          {formatNumber(bucket.parkedCount)}
                        </Badge>
                      </TableCell>
                      <TableCell>
                        <span className="text-sm">{formatSlot(bucket.nextSlotUtc)}</span>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  );
}
