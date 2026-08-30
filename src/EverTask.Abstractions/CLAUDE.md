# EverTask.Abstractions

Refer to the root CLAUDE.md for project-wide rules.

Contracts-only package (`IEverTask`, `ITaskDispatcher`, `IEverTaskHandler<T>`, `EverTaskHandler<T>`, `IRetryPolicy`,
`IRateLimitedTask`): application code references it without pulling in the runtime.

**Every public type lives in the flat `EverTask.Abstractions` namespace** (4.0+; `EverTask.Resilience` is gone). The
feature folders (`Handler/`, `Resilience/`, `RateLimiting/`, `Recurring/`, `Guids/`) are organizational only — the
`.csproj.DotSettings` marks them as non-namespace-providers, so never "fix" a file's namespace to match its folder.

## Payload contract (System.Text.Json since v3.10)

Payloads go through `src/EverTask/Serialization/EverTaskJson.cs` (isolation rationale in the root CLAUDE.md). Use
primitives, `string`, `Guid`, `DateTimeOffset`, enums, collections, nested records; never entities, services,
DbContexts or circular graphs.
- **Public PROPERTIES only.** Public *fields* are NOT serialized (`IncludeFields` is deliberately off).
- **Newtonsoft attributes are NOT honored** — `[JsonProperty]` / `[JsonIgnore]` / `[Newtonsoft.Json.JsonConstructor]`
  are invisible to STJ. Use PascalCase names; do not rely on Newtonsoft rename/ignore/ctor-select.
- A property with only a **non-public setter and no matching ctor parameter** is dropped on read.
- `object` / `Dictionary<string, object>` values come back as `JsonElement`, not boxed primitives / `JObject`.
- A property typed as an **abstract base / interface** is not round-tripped by default (derived members dropped on
  write, read throws). Escape hatch: `[JsonPolymorphic]` + `[JsonDerivedType(typeof(Sub), "alias")]` on the base — a
  CLOSED declared alias set, not arbitrary type loading, so the gadget-deserialization isolation holds. NEVER rely on
  Newtonsoft's `TypeNameHandling`. Pinned by `Serialization/PolymorphicPayloadTests.cs` and
  `IntegrationTests/PolymorphicPayloadRecoveryIntegrationTests.cs`.
- **Native AOT / trimming**: `EverTaskJson` is reflection-based (no `TypeInfoResolver`), so it is NOT compatible with
  Native AOT or `JsonSerializerIsReflectionEnabledByDefault=false` — (de)serialization throws there whatever the
  payload shape, and a consumer's own `JsonSerializerContext` has no effect since the isolated options never consult
  it.

## Payload contract analyzer (issue #14)

The Roslyn analyzer in `analyzers/EverTask.Analyzers` is **bundled into this package** (packed to
`analyzers/dotnet/cs`), so it lights up wherever `IEverTask` is referenced — no opt-in, no runtime dependency. It
validates the contract above at compile time for every `IEverTask` type and the in-source types reachable through
their serialized members (closure walk, visited-set + depth bound).

| ID | Default | Trigger | Code fix |
|----|---------|---------|----------|
| ET0001 | Warning | Public instance field on a payload (not `[JsonInclude]`) | field → auto-property |
| ET0002 | Warning | Property with non-public/absent setter **and** no matching ctor parameter (init & record positional are OK) | add public setter |
| ET0003 | Warning | `Newtonsoft.Json` attribute on type/member/ctor | remove, or map `[JsonProperty]`→`[JsonPropertyName]` / Newtonsoft `[JsonIgnore]`→STJ `[JsonIgnore]` |
| ET0004 | Warning | Abstract/interface property without `[JsonPolymorphic]`+`[JsonDerivedType]` (unwraps `Nullable`/collections) | scaffold the attributes on the base |
| ET0005 | Info | `object` / `dynamic` / `Dictionary<string,object>` property | — |
| ET0006 | **Disabled** | Non-round-trippable type (delegate, `Stream`, `Type`, `IntPtr`, `CancellationToken`, `DbContext`, `ValueTuple`) — heuristic | — |
| ET0007 | Warning | ≥2 public constructors, none parameterless or `[JsonConstructor]` → STJ throws on recovery (records & single-ctor are OK) | — |
| ET0008 | Warning | net8.0 compilation sets `EnableOpenApiDocument = true` or calls `AddMonitoringApiScalar()` — both no-ops there; separate `MonitoringOpenApiAnalyzer`, category `EverTask.Monitoring` | — |
| ET0009 | Warning | Compile-time-constant retry delay, timeout or audit cleanup interval above the maximum timer duration (`uint.MaxValue - 1` ms, ~49.7 days); separate `TimerDelayLimitAnalyzer`, category `EverTask.Resilience` | — |
| ET0010 | Warning | `InTimeZone` on a fluent chain that is provably Elapsed-only, which `RecurringTask.Validate()` refuses; separate `ScheduleTimeZoneAnalyzer`, category `EverTask.Scheduling` | — |

Each rule is suppressible/promotable per-member via `dotnet_diagnostic.ETxxxx.severity` in `.editorconfig`.
- **ET0010 stays silent on a chain carrying `Except`/`ExceptWeekends` anywhere** (either side of
  `InTimeZone`): calendar exclusions make a zone legal on an Elapsed grid, and the zero-false-positive bar
  wins over completeness.
- **ET0010 mirrors `RecurringTask.IsCalendarAnchored()`, not the fluent method names**: a Day/Week/Month interval is
  calendar-anchored on its own, and `OnHours()` is NOT (it populates no hour selector — see
  `src/EverTask/Scheduler/Recurring/CLAUDE.md` gotchas 10 and 16). It reports only a chain it can follow back to the
  `Schedule()`/`Then()` that started it; anything unprovable is left to the runtime check.
- **Maintainers**: keep this table, `DiagnosticDescriptors.cs` and `AnalyzerReleases.Unshipped.md` in lockstep
  (RS2002). The two analyzer DLLs target `netstandard2.0` and reference `Microsoft.CodeAnalysis.*` with
  `PrivateAssets="all"`, staying out of the package's dependency closure. Pinned by `test/EverTask.Analyzers.Tests`.

## EverTaskHandler&lt;TTask&gt;

| Property | Default | Notes |
|----------|---------|-------|
| `RetryPolicy` | `null` | Resolution chain in `WorkerExecutor`: handler override → the declared queue's `DefaultRetryPolicy` → global (`LinearRetryPolicy(3, 500ms)`) |
| `Timeout` | `null` | Same chain |
| `RateLimitPolicy` | `null` (no limit) | Per-key throttling; key from `IRateLimitedTask` or a `GetRateLimitKey()` override. A DIM on `IEverTaskHandlerOptions`, so external implementors keep compiling. See `src/EverTask/RateLimiting/CLAUDE.md` |

Handlers are auto-registered as **transient** (`HandlerRegistrar` uses `TryAddTransient`) and resolved per task inside
the worker's own scope.

## Per-delivery injection (`Logger`, `Context`)

`ITaskLogCapture Logger` and `ITaskExecutionContext Context` are `protected` on the base class, set through EXPLICIT
implementations of `SetLogCapture`/`SetExecutionContext` — the worker reaches both through compiled delegates cached
per handler type (`WorkerExecutor.HandlerOptionsCache`), never per-execution reflection. `SetExecutionContext` is a
**default interface member with an empty body**, so a handler implementing `IEverTaskHandler<T>` directly keeps
compiling and the DIM is still reached: the injector calls through the interface.
- `Context` has a nullable backing field and a getter that THROWS when read before injection: no `null!`
  (warnings-as-errors) and no silently empty context in a constructor.
- Only `Attempt` moves while a delivery is alive (`TaskExecutionContext.SetAttempt`, volatile). Everything else is
  fixed at creation, so a handler may cache it — but not `Attempt`.
- Anything that is not the handler reads the same instance through the singleton `ITaskExecutionContextAccessor`
  (`AsyncLocal`). Singleton on purpose: an eager handler's graph is built in the DISPATCHER's scope, where a scoped
  accessor would never see the delivery.
- **Gotcha**: terminal rate-limit rejections (horizon exceeded, `Discard`) deliver a typed
  `RateLimitRejectedException` to `OnError`; plain deferrals invoke NO callback. The rate-limit key is a throttling
  key — never reuse the dispatch `taskKey` for it.

## Retry policies

`LinearRetryPolicy` and `ExponentialRetryPolicy` derive from `RetryPolicyBase<TPolicy>`: the `Execute` loop, exception
filtering and `OnRetry` handling live there once.
- Filter precedence, highest first: custom predicate (`.HandleWhen(...)`) → whitelist (`.Handle<T>()`) → blacklist
  (`.DoNotHandle<T>()`) → the default. Predefined sets: `.HandleTransientDatabaseErrors()`,
  `.HandleTransientNetworkErrors()`, `.HandleAllTransientErrors()`.
- `ExponentialRetryPolicy(retryCount, initialDelay, backoffFactor = 2.0, maxDelay = null, useJitter = false)`:
  delay(n) = `initialDelay × backoffFactor^(n-1)`, capped at `maxDelay` and always at the `Task.Delay` limit (~49.7
  days); jitter is ±20% per attempt, computed at execution time and re-capped.
- Keep the non-generic `this LinearRetryPolicy` overloads of `HandleTransient*` in `RetryPolicyExtensions.cs` next to
  the generic ones: they are the 3.11.0 binary surface and the only ones a `LinearRetryPolicy` subclass can bind.
- Mixing `Handle<T>()` and `DoNotHandle<T>()` throws; matching uses `Type.IsAssignableFrom()`, so derived exceptions
  match; the `OnRetry` callback is 1-based (first retry = attempt 1).

**Recurring builder**: `MaxRuns(int)` returns `void` — call it LAST in the chain. Interval gotchas:
`src/EverTask/Scheduler/Recurring/CLAUDE.md`; usage examples: `docs/recurring-tasks.md`.
