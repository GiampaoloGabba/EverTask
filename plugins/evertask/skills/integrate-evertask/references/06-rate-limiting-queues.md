# 06 — Keyed rate limiting, multi-queue, scalability, sharded scheduler

## Keyed rate limiting (v3.7+)

Throttle a task type per key (tenant / account / resource) — useful when a handler calls an
external API with per-key limits. **It limits frequency, not parallelism.**

Declare the policy on the handler and supply the key on the task:

```csharp
public record SyncTenantData(Guid TenantId) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => TenantId.ToString();
}

public class SyncTenantDataHandler(IApi api) : EverTaskHandler<SyncTenantData>
{
    public override RateLimitPolicy? RateLimitPolicy =>
        new RateLimitPolicy(permits: 15, period: TimeSpan.FromMinutes(1));
    public override async Task Handle(SyncTenantData t, CancellationToken ct)
        => await api.SyncAsync(t.TenantId, ct);
}
```

Alternatively key on the handler without touching the task:
`public override string? GetRateLimitKey(SyncTenantData t) => t.TenantId.ToString();`

### `RateLimitPolicy`

```csharp
new RateLimitPolicy(int permits, TimeSpan period)   // permits > 0, period > 0
```

| init-only property | Default | Meaning |
|---|---|---|
| `Burst` | `= Permits` | Max back-to-back executions after idle. `1` = strict even spacing (`period/permits` apart). |
| `ThrottleRetries` | `true` | Retries re-acquire budget through the gate (before the per-attempt timeout). Not inline: a far slot **re-parks** the task → fires via redelivery, **restarting** retry attempt numbering. Set false for cheap local-only retries. |
| `StartEmpty` | `false` | New key starts full. `true` to avoid a burst right after restart. |
| `MaxReservationHorizon` | `1 hour` | Backlog beyond this → terminal per `OverflowBehavior`. |
| `MaxInSlotWait` | `1 second` | **No-op — retained for binary compatibility only.** The gate never waits inline on the consumer anymore: every over-budget task (near or far slot) is re-parked to the scheduler and fires at its reserved slot via redelivery. (An inline wait would head-of-line-block the single consumer.) |
| `OverflowBehavior` | `WaitForCapacity` | or `Discard` (immediate terminal `Failed`). |

Model: GCRA token bucket per `(taskType, key)` — different task types never share a key's budget.
Steady emission = `period/permits`; `Burst` caps idle accumulation.

**Deferral invariant:** a deferral writes **nothing** to storage; the parked task stays `Queued`
(already covered by recovery). Terminal rejections (horizon exceeded or `Discard`) call `OnError`
with `RateLimitRejectedException`; plain deferrals call no callback.

**Multi-instance:** rate limiting is **per-instance** — divide the external budget across instances
(15/min API, 3 instances → ~5/min each). A distributed limiter seam exists
(`services.AddSingleton<IKeyedRateLimiter, MyImpl>()` before `AddEverTask`) but no built-in
distributed implementation ships. **Fail-open:** if a custom limiter *throws* (e.g. Redis down),
the gate logs a warning and runs the task **unthrottled** — a limiter outage never fails a task
(`MaxTrackedKeys` overflow fails open the same way). Only shutdown `OperationCanceledException`
propagates (task stays recoverable).

**Observing rate-limit state in code:** because deferrals write nothing to storage, the only way to
see throttling/backpressure programmatically is to inject `IRateLimiterIntrospection` (read-only,
single-node): `ParkedTaskCount`, `MaxParkedTasks`, `TrackedKeyCount`, `FailOpenCount`,
`GetParkedSnapshot()` (per-(queue,key) parked counts + next slot), `GetThrottledUntil(taskId)`. Use
it for health checks / metrics export / alerting. (The monitoring dashboard surfaces the same via
`GET /evertask-monitoring/api/rate-limits`.)

Global infrastructure knobs (`SetRateLimiterOptions`) — see `01-setup.md`.

## Multi-queue

Isolate workloads (e.g. critical payments vs bulk emails) with different parallelism/capacity.

```csharp
.ConfigureDefaultQueue(q => q.SetMaxDegreeOfParallelism(5).SetChannelCapacity(100))
.AddQueue("high-priority", q => q
    .SetMaxDegreeOfParallelism(10).SetChannelCapacity(200).SetFullBehavior(QueueFullBehavior.Wait))
.AddQueue("background", q => q
    .SetMaxDegreeOfParallelism(2).SetChannelCapacity(50).SetFullBehavior(QueueFullBehavior.FallbackToDefault))
```

Route a handler with `public override string? QueueName => "high-priority";`.

`QueueConfiguration` methods: `SetMaxDegreeOfParallelism`, `SetChannelCapacity` /
`SetChannelOptions(BoundedChannelOptions)`, `SetDefaultRetryPolicy`, `SetDefaultTimeout`,
`SetFullBehavior`.

**New-queue defaults** (via `AddQueue`): parallelism `1` (sequential — set it explicitly!),
capacity `500`, `FullBehavior = FallbackToDefault`.

Calling `AddQueue` again with the same name **replaces** the previous configuration. Raw object
defaults differ: `new QueueConfiguration()` starts as `Name = "default"`, parallelism `1`,
channel capacity `2000` with `FullMode = Wait`, `SingleReader = false`, `SingleWriter = false`,
`AllowSynchronousContinuations = false`, and `FullBehavior = FallbackToDefault`.

`QueueFullBehavior` (immediate dispatches only; scheduler-triggered dispatches use non-blocking
write + backoff): `Wait` (block, cancellable) | `FallbackToDefault` (non-blocking try on target,
then re-route to the `default` queue with blocking `Wait` backpressure — the task then runs on the
default queue, so it does **not** honor the target's parallelism/isolation; if target *is* default
it's plain `Wait`) | `ThrowException` (`QueueFullException`; task stays `WaitingQueue`, recovered on
restart).

Routing: `null` → "default"; recurring without `QueueName` → "recurring"; fallback still applies
the rate-limit policy (rate limiting is per task type, not per queue). Retry/timeout resolution uses
handler → queue default → global default. A task rerouted by `FallbackToDefault` keeps its **declared**
queue's retry/timeout config; an **unregistered** `QueueName` falls back to `default` for both routing
and retry/timeout config.

## Scalability — two independent axes

- **Execution throughput** is storage-bound (DB round-trips/task). Indicative (Ryzen 9 7950X,
  .NET 10, audit off): Postgres ~2,500/s, SQLite ~200/s (single writer; parallelism doesn't help).
  Levers: faster DB, lower audit level, fewer round-trips. **Neither multi-queue nor the sharded
  scheduler raises this.**
- **Scheduling load** (`Schedule()` rate, in-memory timed count) is CPU/contention-bound — the
  sharded scheduler targets this only.

Recommendations: start with defaults; measure with the dashboard; tune queue parallelism → add
multi-queue → enable sharded scheduler only if profiling shows scheduler lock contention.
Horizontal scale = shared SQL/Postgres DB; tasks distribute naturally (mind per-instance rate limits).

## Sharded scheduler

```csharp
opt.UseShardedScheduler();           // auto: max(4, ProcessorCount) shards
opt.UseShardedScheduler(shardCount: 8);  // explicit (recommended 4–16)
```

Shards the in-memory priority queue across N independent timers/locks. ~300 bytes + 1 thread per
shard; failure-isolated; seamless (same `IScheduler`, no storage/handler changes). Enable only for
sustained high `Schedule()` rates with proven contention — not to speed up execution.

## Wizard decision points

- Per-key rate limit? → ask the API limit (N per T per key); bursts vs strict (`Burst`); multi-instance
  (divide budget); restart-burst risk (`StartEmpty`); retries count (`ThrottleRetries`); overflow
  (`WaitForCapacity` vs `Discard`).
- Workloads with different priority/resource needs? → named queues; I/O-bound (higher parallelism)
  vs CPU-bound; full behavior (`FallbackToDefault`/`Wait`/`ThrowException`).
- Recurring needs different perf from one-shots? → `ConfigureRecurringQueue`.
- High scheduling rate with proven contention? → `UseShardedScheduler` (rare).
