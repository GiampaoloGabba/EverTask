# 04: Resilience: retry, exception filtering, timeout, cancellation, shutdown

## Retry policy

Resolution chain (highest priority first): **handler `RetryPolicy` → queue default → global default**.

```csharp
// Global
opt.SetDefaultRetryPolicy(new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(500)));
// Per-queue
.AddQueue("slow", q => q.SetDefaultRetryPolicy(new LinearRetryPolicy(5, TimeSpan.FromSeconds(2))))
// Per-handler (override the virtual)
public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(5, TimeSpan.FromSeconds(1));
```

**Default** (when nothing overrides): `LinearRetryPolicy(3, 500ms)`: 3 retries (4 total), retry
everything **except** `OperationCanceledException` and `TimeoutException` (hardcoded fail-fast).
A `null` handler `RetryPolicy` means "defer to queue/global", **not** "no retries".

### `LinearRetryPolicy` constructors

```csharp
new LinearRetryPolicy(int retryCount, TimeSpan retryDelay)   // uniform delay; both > 0
new LinearRetryPolicy(TimeSpan[] retryDelays)                // per-attempt delays (≥1 element, all > 0)
```

```csharp
// "Exponential-ish" via explicit per-attempt delays:
public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(new[]
    { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8) });
```

For true exponential/jitter, implement `IRetryPolicy` (e.g. wrapping Polly); its `Execute(...)`
runs the action.

## Exception filtering (fluent on `LinearRetryPolicy`)

```csharp
// Whitelist: retry ONLY these (and derived):
.Handle<DbException>().Handle<HttpRequestException>()
.Handle(typeof(DbException), typeof(IOException))            // params overload

// Blacklist: retry all EXCEPT these:
.DoNotHandle<ArgumentException>().DoNotHandle<ValidationException>()

// Predicate: takes precedence over whitelist/blacklist:
.HandleWhen(ex => ex is HttpRequestException h && (int?)h.StatusCode >= 500)

// Predefined sets:
.HandleTransientDatabaseErrors()   // DbException (+ TimeoutException, but see note)
.HandleTransientNetworkErrors()    // HttpRequestException, SocketException, WebException, TaskCanceledException
.HandleAllTransientErrors()        // both combined
```

Priority: (1) `OperationCanceledException`/`TimeoutException` always fail-fast → (2) `HandleWhen`
predicate → (3) whitelist → (4) blacklist → (5) default retry-all. **Mixing whitelist and
blacklist throws `InvalidOperationException`.**

> Note: the hardcoded guard means `TimeoutException` stays non-retryable even if a predefined set
> nominally includes it, so `HandleTransientDatabaseErrors()` effectively whitelists `DbException`
> only. To retry on timeout-like conditions, use `HandleWhen`.

## `OnRetry` callback

```csharp
public override ValueTask OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay)
{
    Logger.LogWarning(exception, "Retry #{Attempt} after {Ms}ms", attemptNumber, delay.TotalMilliseconds);
    return ValueTask.CompletedTask;
}
```

Fires **after** the delay, **before** each retry (`attemptNumber` 1-based). Not on the first
attempt nor on success. Exceptions inside it are logged but never block the retry.

## Timeout

Resolution chain: **handler `Timeout` → queue default → global default** (default `null` = none).

```csharp
opt.SetDefaultTimeout(TimeSpan.FromMinutes(5));                 // global
.AddQueue("quick", q => q.SetDefaultTimeout(TimeSpan.FromSeconds(30)))  // per-queue
public override TimeSpan? Timeout => TimeSpan.FromSeconds(30);  // per-handler
```

On expiry the handler's token is cancelled and the framework wraps it as `TimeoutException`
(not retried by default). The rate-limit budget wait happens **before** the timeout window starts,
so a 30s timeout always covers 30s of real execution.

## CancellationToken into `Handle`

The token is signalled on timeout, on `dispatcher.Cancel(taskId)`, or on host shutdown (it is
linked to the service token). Cooperate:

```csharp
public override async Task Handle(BatchTask task, CancellationToken ct)
{
    foreach (var item in task.Items)
    {
        ct.ThrowIfCancellationRequested();
        await ProcessItemAsync(item, ct);   // pass ct to all async calls
    }
}
```

Let `OperationCanceledException` propagate: it routes to cancelled-by-user or cancelled-by-service.

## Graceful shutdown

On shutdown, an in-flight task is marked **`ServiceStopped`** and re-queued on next startup
(automatic, no config). For long tasks, support resumption: encode progress in the payload and
re-dispatch with updated progress, and/or clean up before re-throwing:

```csharp
catch (OperationCanceledException) when (ct.IsCancellationRequested)
{
    await SavePartialProgressAsync();
    throw;   // marks ServiceStopped → recovered on restart
}
```

`SetThrowIfUnableToPersist(true)` (default) ensures a task that cannot be persisted fails loudly
rather than being silently lost (recovery can't help an unpersisted task).

## Error observation (`OnError`)

Fires after retries are exhausted (`Failed`), on timeout (`TimeoutException`), cancellation
(`OperationCanceledException`), or terminal rate-limit rejection (`RateLimitRejectedException`).
The exception is **unwrapped** from the retry `AggregateException`: type-switch on the real one:

```csharp
public override ValueTask OnError(Guid taskId, Exception? exception, string? message)
{
    Logger.LogError(exception, "Task {Id} failed: {Msg}", taskId, message);
    if (exception is not OperationCanceledException) _alerts.Notify(taskId, exception);
    return ValueTask.CompletedTask;
}
```

For cross-cutting observation across all tasks, subscribe to
`IEverTaskWorkerExecutor.TaskEventOccurredAsync` instead (see `07-monitoring-logging.md`).

## Wizard decision points

1. Retry at all? Default is yes (3×). For run-once, a custom `IRetryPolicy` that executes once.
2. How many / what backoff? `LinearRetryPolicy(n, delay)` or per-attempt array.
3. Which exceptions? none-filter (default) / whitelist / blacklist / predicate (can't mix W+B).
4. Per-execution timeout? Set on handler/queue/global; add a safety buffer.
5. Visibility into retries/failures? Override `OnRetry` / `OnError`.
6. Shared failure characteristics across task types? Set queue-level defaults instead of repeating.
