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

**CRITICAL**: Tasks are persisted using **System.Text.Json** (migrated from Newtonsoft.Json in v3.9). The
serializer is the internal, isolated `EverTaskJson` (private static `JsonSerializerOptions`, L33). It READS
leniently for backward-compat with legacy Newtonsoft rows (quoted numbers, string-named enums via a tolerant
converter) but WRITES the historical numeric form (enums as numbers) for byte-parity.

✅ **Use**: Primitives, `string`, `Guid`, `DateTimeOffset`, public **properties** with public setters (or a
matching ctor parameter), enums, collections, nested records.
❌ **Avoid**: Complex graphs, circular refs, entities, services, DbContexts.

**Payload contract (STJ specifics — differ from Newtonsoft):**
- **Public PROPERTIES only.** Public *fields* are NOT serialized (`IncludeFields` is deliberately off).
- **Newtonsoft attributes are NOT honored** — `[JsonProperty]` / `[JsonIgnore]` / `[Newtonsoft.Json.JsonConstructor]`
  are ignored by STJ. Use PascalCase property names; do not rely on Newtonsoft rename/ignore/ctor-select.
- A property with only a **non-public setter and no matching ctor parameter** is dropped on read.
- `object` / `Dictionary<string, object>` values come back as `JsonElement` (not boxed primitives / `JObject`).
- A nested property typed as an **abstract base / interface** is not round-tripped BY DEFAULT (the derived
  members are dropped on write and read throws). **Supported escape hatch:** declare STJ polymorphism on the
  base type with `[JsonPolymorphic]` + `[JsonDerivedType(typeof(Sub), "alias")]`. The discriminator is a
  CLOSED, declared set of aliases (NOT arbitrary type loading), so the concrete subtype round-trips while the
  L33 gadget-deserialization isolation holds. Pick a discriminator name (e.g. `$kind`) and NEVER rely on the
  old Newtonsoft `TypeNameHandling`. Pinned by `test/EverTask.Tests/Serialization/PolymorphicPayloadTests.cs`
  + `IntegrationTests/PolymorphicPayloadRecoveryIntegrationTests.cs`.

  ```csharp
  [JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
  [JsonDerivedType(typeof(EmailChannel), "email")]
  [JsonDerivedType(typeof(SmsChannel),   "sms")]
  public abstract class NotifyChannel { }
  public sealed class EmailChannel : NotifyChannel { public string Address { get; set; } = ""; }
  public record NotifyTask(NotifyChannel Channel) : IEverTask;   // round-trips concrete subtype + members
  ```

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
