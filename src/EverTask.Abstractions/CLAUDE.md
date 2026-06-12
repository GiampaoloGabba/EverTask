# EverTask.Abstractions

## Purpose

Lightweight contracts package for EverTask. Application code references this without pulling full runtime (same pattern as MediatR.Contracts vs MediatR).

**Dependencies**: `Microsoft.Extensions.Logging.Abstractions` + `UUIDNext` (database-friendly UUIDv7 generation for `IGuidGenerator`)

## Key Interfaces

| Interface | Purpose | MediatR Equivalent |
|-----------|---------|-------------------|
| `IEverTask` | Marker for task requests | `INotification` |
| `ITaskDispatcher` | Entry point for dispatch/cancel | `IMediator` |
| `IEverTaskHandler<T>` | Task execution contract | `INotificationHandler<T>` |
| `IRetryPolicy` | Custom retry logic | - |
| `IRateLimitedTask` | Carries the per-key throttling key (v3.7+) | - |

## Serialization Guidelines

**CRITICAL**: Tasks are persisted using Newtonsoft.Json.

✅ **Use**: Primitives, `string`, `Guid`, `DateTimeOffset`
❌ **Avoid**: Complex graphs, circular refs, entities, services, DbContexts

**Pattern**: Store IDs, not entities (`Guid OrderId` ✅, `Order Order` ❌)

## Base Classes

### EverTaskHandler<TTask>

| Property | Default | Notes |
|----------|---------|-------|
| `RetryPolicy` | `LinearRetryPolicy(3, 500ms)` | Override for custom retry |
| `Timeout` | `null` | Set per handler |
| `RateLimitPolicy` | `null` (no limit) | Per-key throttling (v3.7+); key from `IRateLimitedTask` or a `GetRateLimitKey` override. Declared as DIM on `IEverTaskHandlerOptions` so external implementors keep compiling. See `src/EverTask/RateLimiting/CLAUDE.md` |

**Override Points**: `Handle()` (required), `OnStarted/OnCompleted/OnError/OnRetry` (optional), `GetRateLimitKey()` (optional)

**Gotcha**: terminal rate-limit rejections (horizon exceeded, `Discard`) deliver a typed `RateLimitRejectedException` to `OnError`; plain deferrals invoke NO callback. The rate-limit key is a throttling key — never reuse the dispatch `taskKey` for it.

### LinearRetryPolicy

**Exception Filtering** (v1.6.0+):

| Pattern | Code | Priority |
|---------|------|----------|
| Custom predicate | `.HandleWhen(ex => ex is HttpRequestException http && http.StatusCode >= 500)` | 1 (highest) |
| Whitelist | `.Handle<DbException>().Handle<HttpRequestException>()` | 2 |
| Blacklist | `.DoNotHandle<ArgumentException>().DoNotHandle<ValidationException>()` | 3 |
| Default | Retry all except `OperationCanceledException`, `TimeoutException` | 4 (fallback) |

**Predefined Sets**:
- `.HandleTransientDatabaseErrors()` — DbException, TimeoutException
- `.HandleTransientNetworkErrors()` — HttpRequestException, SocketException, WebException, TaskCanceledException
- `.HandleAllTransientErrors()` — Combines above

**Gotchas**:
- Cannot mix `Handle<T>()` and `DoNotHandle<T>()` (throws)
- Uses `Type.IsAssignableFrom()` for derived types
- `OnRetry` callback is 1-based (first retry = attempt 1)

## Recurring Task Builder

**Common Scenarios**:

| Scenario | Code |
|----------|------|
| Daily at 3 AM | `r => r.Schedule().EveryDay().AtTime(new TimeOnly(3, 0))` |
| Every 5 minutes | `r => r.Schedule().Every(5).Minutes()` |
| Cron expression | `r => r.Schedule().UseCron("0 0 * * *")` |
| Run now + hourly | `r => r.RunNow().Then().EveryHour()` |
| Max runs + end date | `r => r.Schedule().Every(30).Minutes().MaxRuns(10).RunUntil(endDate)` |

For interval gotchas, see `src/EverTask/Scheduler/Recurring/CLAUDE.md`.

## DI Registration

```csharp
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddSqlServerStorage(connectionString);
```

Handlers auto-registered as scoped services.

## 🔗 Test Coverage

**When modifying retry policies or handlers**:
- Update: `test/EverTask.Tests/RetryPolicyTests.cs`
- Verify exception filtering in: `test/EverTask.Tests/ExceptionFilteringTests.cs`
- Integration test patterns: `test/EverTask.Tests/IntegrationTests/`

**When adding new handler options**:
- Add test case in `test/EverTask.Tests/HandlerTests.cs`
- Verify lifecycle callbacks in `test/EverTask.Tests/LifecycleTests.cs`
