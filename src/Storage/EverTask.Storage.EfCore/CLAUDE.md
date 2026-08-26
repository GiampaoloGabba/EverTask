# EverTask.Storage.EfCore

Refer to the root CLAUDE.md for project-wide rules.

Base EF Core storage for every relational provider; never used standalone.

- **Contexts come from a POOLED factory** (`ITaskStoreDbContextFactory` over `AddPooledDbContextFactory`), one
  leased per operation, never shared. A pooled context admits only the single `DbContextOptions<T>` ctor
  parameter, so the schema travels in the options via `UseEverTaskSchema` (`EverTaskSchemaExtension`) instead
  of being injected. No `IServiceScopeFactory.CreateScope()` exists in this layer.
- **`Persist` and `UpdateTask` normalize the row's timestamps to offset zero** (`NormalizeTimestampsToUtc`,
  F1/#37). They are the two public writes a caller drives directly, and SQLite compares a `DateTimeOffset` as
  the TEXT it stored, offset included — so a row written at `+02:00` lost every cursor compare-and-swap
  against the same instant in UTC, for ever. The CAS operations already normalize their own operands; the
  stored value was the half still free to disagree. `MemoryTaskStorage` does the same so the two stores round
  a row trip identically. Pinned by the two `…_non_utc_offset` facts of the shared contract suite.
- `RetrievePending` is the startup recovery filter — whatever it excludes is silently lost on restart, so
  `WaitingQueue` (persisted, not yet handed to a channel) and recurring `Completed`/`Failed` rows with
  `NextRunUtc != null` must stay in it.
- **A new recoverable status must be added in FOUR places**: `QueuedTask.IsRecoverableForExecution`
  (canonical, in `src/EverTask/Storage/QueuedTask.cs`), `EfCoreTaskStorage.RecoverableForExecutionQuery`
  (EF-translatable mirror), the inline list in `SqliteTaskStorage.RetrievePending`, and a NEW Postgres
  migration — the partial index `IX_QueuedTasks_Recovery` bakes `WHERE Status IN (…)` into
  `Migrations/20260616162141_Initial.cs`, so a missing status makes Postgres recovery skip those rows
  silently. `MemoryTaskStorage` keeps no copy.
- **A recovery page is a UNION**: `RecoveryPageQuery` composes the execution predicate with
  `SeriesToFinalizeQuery` through the parameter-rebinding `Compose` — composed, never re-spelled, so the two
  copies cannot drift. The execution predicate is itself composed, from `RecoverableStatusAndBudget` (the
  half EVERY provider translates) and the temporal term: that split is what lets SQLite put the first half in
  the WHERE clause of its own conditional UPDATE while deciding only the second in memory.
  `TrySetQueuedIfRecoverable` uses ONLY the execution half, and on a relational provider it is always a
  compare-and-swap. `TrySetQueuedClientSideAsync` is the read-then-write shape and is NOT one — it exists for
  EF Core InMemory, which can express no conditional UPDATE at all.
- **The two clock-carrying overloads hand back to a derived legacy override.** Both public entry points are
  thin: the query lives in a private `…Core(nowUtc, …)`, the legacy signature calls it with
  `UtcNowNormalized`, and the `nowUtc` one calls it only when the concrete type does NOT override the legacy
  signature (probed once per type, `LegacyOverrides`). A base-class virtual binds statically, so without that
  probe a provider that overrode only the pre-4.0 signature — the shape the shipped scaffolding skill
  produced — would have it silently bypassed by the core, which calls the `nowUtc` overloads exclusively.
  Never make the legacy signature delegate to the `nowUtc` one: an override that calls `base` would then
  bounce between the two forever.
- **The base is never constrained by SQLite**: it always carries the optimized server-side form
  (`BatchDeleteAsync` bounded by `CleanupBatchSize`, `GROUP BY`/`HAVING`, ordered offsets). What SQLite cannot
  translate (`DateTimeOffset` ordering comparisons) is worked around by an `override` there — 12 today — never
  pushed down here. SQL Server and Postgres override 3 hot writes, MySQL those 3 plus `CleanupCompletedTasks`.
- **The durable-occurrence operations are one conditional UPDATE inside one transaction**, and the condition
  lives in the WHERE clause — a preceding SELECT would let two writers both pass the check. They require a
  relational provider (`RequireRelational`), and so **both capabilities answer from the provider**, resolved
  once from a context: on EF Core InMemory — still supported here through the client-side fallbacks in
  `TrySetQueuedIfRecoverable` and `SetStatus` — they are false, because a `true` there would move the refusal
  from dispatch to materialization. A unique-index loss is recognised by CONSTRAINT NAME
  (`UX_QueuedTasks_Occurrence`, or the columns SQLite names instead), never by a generic duplicate-key code,
  which would also swallow a `TaskKey` collision. `Remove` deletes a schedule's occurrences in the same
  transaction: the self-referencing FK is Restrict, so it could not be deleted otherwise.
- **A materialized occurrence has ONE row shape**, `QueuedTask.ApplyOccurrenceContract(scheduleId, version)`:
  the base and the memory store insert the caller's entity, so they stamp it; the procedures and the Postgres
  CTE spell the same columns out in their `INSERT`. Skip it in one of them and the same
  `MaterializeOccurrence(...)` call persists a different row per provider.
- **`CancelSchedule` cancels the exact complement of what recovery requeues** — `WaitingQueue`, `Queued`,
  `Pending` and `ServiceStopped` (R7). Leave one out and an occurrence of a cancelled schedule comes back at
  the next restart and runs. `InProgress` is deliberately left alone: it owns a live delivery.
  It **audits what the UPDATE changed, not what the lookup found.** `ExecuteUpdate` reports a
  count and no ids, and the id lookup and the conditional UPDATE are two statements: an occurrence that
  reached `InProgress` in between is skipped by the UPDATE and must not get a `Cancelled` audit row for a
  status it never took. The candidates are re-read inside the same transaction, which is exact only while
  writers are serialized (SQLite); the procedures and the Postgres CTE get the same set from `OUTPUT` /
  `RETURNING`, which EF cannot express, and that is the CONTRACT for any provider with concurrent writers.
- **`TryAdvanceScheduleCursor` is the one write a SKIPPED slot needs**: `CursorCas` plus a single
  `SetProperty(NextRunUtc)`, no transaction, no run counted, no audit — nothing executed. Every slot that
  survives the misfire policy carries the cursor forward inside `MaterializeOccurrence` instead, so this is
  only reached when nothing survives at all. One statement, so no provider override is warranted.
- **A null expected cursor never reaches that WHERE** — `CursorCas` takes a NON-nullable one. EF rewrites a
  null parameter to `NextRunUtc IS NULL`, which matches exactly the finalized and poisoned schedules the
  compare-and-swap exists to exclude, and the caller that retried with the cursor it read back would insert
  an occurrence on a Completed series and hand it a cursor again. `MaterializeOccurrence` classifies that
  case (`ParentInactive` / `CursorMoved`) before it writes, like the procedures and the Postgres CTE do
  server-side.
- `AuditCleanupHostedService` only interprets the retention policy — any knob `<= 0` is disabled, and an
  active log retention makes BOTH row-deleting passes (`CleanupCompletedTasks` and
  `CleanupTerminalOccurrences`) preserve rows that still own execution logs. The occurrence window is
  typically much shorter than the log window, so without that guard it cascade-deletes logs the log retention
  deliberately kept.
  - **The AUDIT trail needs the same guard, and for a stronger reason**: `FK_StatusAudit_QueuedTasks` and
    `FK_RunsAudit_QueuedTasks` cascade on delete, so purging an occurrence destroys the transitions and runs
    under it. `CleanupCompletedTasks` has always refused a row with any audit left; the occurrence pass now
    carries the same refusal through `preserveTasksWithAudits`, set when a status- or runs-audit window is
    active. Seven days of occurrence retention against ninety of error retention erased a failure on day
    eight, and the cleanup line reported an occurrence count and nothing else. Both flags are parameters
    because SQLite and MySQL override the method, and an override that drops one silently purges what the
    other window kept.
- A new `ITaskStorage` method goes in `test/EverTask.Tests.Storage/EfCore/EfCoreTaskStorageTestsBase.cs` and
  then runs on all four providers. New provider: use the `new-relational-storage-provider` skill.
