---
name: new-relational-storage-provider
version: 1.0.0
description: |
  Playbook for adding a new EF Core RELATIONAL storage provider to EverTask
  (MySQL, MariaDB, Oracle, CockroachDB, …) by inheriting EverTask.Storage.EfCore.
  Use when the user asks to add/scaffold/implement a new database storage provider,
  port the storage layer to another relational DB, or create an EverTask.Storage.X
  package. It encodes the repeatable scaffold AND — critically — the MANDATORY
  per-database verification matrix so the agent never assumes "it just inherits the
  base": each provider differs on DateTimeOffset/uuid/ExecuteDelete translation,
  schema concept, identifier casing, and the Phase-2 hot-write mechanism. Do NOT use
  for the in-memory or a non-EF/NoSQL store (that is the custom-storage path).
---

# Add a new EF Core relational storage provider to EverTask

EverTask's storage is an EF Core base (`EverTask.Storage.EfCore.EfCoreTaskStorage`) that already
carries the **optimized, server-side** implementation any *transactional/relational* provider can run.
A new provider is mostly a thin shell that inherits it — **but only after you PROVE, per database, that
the base's LINQ actually translates server-side**. Postgres was nearly free because it is almost
identical to SQL Server; MySQL/Oracle are NOT — they break specific assumptions. This skill makes you
verify each axis instead of assuming.

> Golden rule: **inherit the base; override ONLY the methods whose LINQ a given provider cannot translate**
> (the SQLite pattern). Never push a provider workaround into the base. Never declare "zero overrides"
> without the captured SQL + a green `EfCoreTaskStorageTestsBase` run on a real container.

## Reference implementations (read these first)

| Reference | What it teaches |
|---|---|
| `src/Storage/EverTask.Storage.Postgres/` | Clean relational provider: inherits base with **zero overrides**, schema-aware migrations (Option B), **writable-CTE** Phase-2. Closest template for a well-behaved relational DB. |
| `src/Storage/EverTask.Storage.SqlServer/` | **Stored-procedure** Phase-2 + schema-aware migration assembly. Template when the DB has no writable CTEs (e.g. MySQL). |
| `src/Storage/EverTask.Storage.Sqlite/` | The **override pattern** for constructs a provider can't translate (DateTimeOffset ordering, `Take().ExecuteDelete`, date-filtered stats). Template for any axis that fails the matrix. |
| `src/Storage/EverTask.Storage.EfCore/EfCoreTaskStorage.cs` + its `AGENTS.md` | The base + the "New EF Core Provider" checklist + which methods are `virtual`. |
| `src/EverTask/Storage/AuditPolicy.cs` | Single source of truth for audit decisions — Phase-2 SQL MUST match it exactly. |
| `review/postgres-provider-plan.md`, `review/postgres-provider-decisions.md` | A fully worked file-by-file plan + decision matrix to mirror. |

## STEP 0 — Mandatory verification matrix (fill this BEFORE scaffolding)

For the target DB, answer every row with evidence (provider docs, EF provider source, a throwaway probe,
or "verify at impl"). This decides which overrides and which Phase-2 mechanism you need. **An unverified
row is a landmine.**

| Axis | How Postgres answered | What to determine for the new DB |
|---|---|---|
| **EF provider package + per-TFM versions** | Npgsql.EntityFrameworkCore.PostgreSQL 8/9/10 | Which package (e.g. MySQL → **Pomelo.EntityFrameworkCore.MySql**, not necessarily Oracle's)? Versions aligned to the repo's EF Core pins (net8/9/10) — check nuget; confirm the `Microsoft.EntityFrameworkCore.Relational` floor ≤ repo pins. |
| **`DateTimeOffset` mapping + ordering translation** | → `timestamptz`, all `<`/`>`/`OrderBy`/keyset translate server-side → **no override** | Does the DB have a timezone-aware type? Do `RunUntil >= now`, the `CreatedAtUtc` keyset, and the cleanup cutoffs translate? **MySQL has no tz type** (Pomelo maps to `datetime(6)`/text) → likely needs the **SQLite override pattern** for `RetrievePending`/`Cleanup*`/stats. |
| **`Guid` keyset `Id.CompareTo(x) > 0`** | `uuid >`, translates; ordering matches UUIDv7 | Native uuid type? Does `Guid.CompareTo` translate? Does the stored ordering match the GUID generator? (MySQL: `char(36)`/`binary(16)` — verify.) |
| **`Take(n).ExecuteDeleteAsync` (cleanup)** | translates to `LIMIT` | Supported? (Postgres/MySQL: `DELETE … LIMIT` ✓. If not → override `Cleanup*` with client-side id-resolution like SQLite.) |
| **Schema concept** | native schema, default `public`; lowercase default `"evertask"` | Does the DB have schemas? **MySQL: a "schema" IS a database** → `SchemaName` semantics change (closer to SQLite's `""`). Oracle: schema == user. |
| **Identifier casing / quoting** | folds unquoted to lowercase, EF quotes → default lowercase to avoid permanent case-sensitivity | Casing rules + any server setting (MySQL `lower_case_table_names` is OS-dependent — a real trap). |
| **GUID generator (`UUIDNext.Database.X`)** | `.PostgreSql` (v7) — byte-wise uuid sort | Which UUIDNext value matches this DB's PK ordering? NEVER copy `.SqlServer` (v8) unless the DB sorts like SQL Server. |
| **Phase-2 hot-write mechanism** | **writable CTE** (single statement, atomic) | Does the DB support **data-modifying CTEs**? **MySQL CTEs are READ-ONLY** → use **stored procedures** (SqlServer template), not CTEs. Oracle: PL/SQL. |
| **Unique index NULL semantics** | NULLs distinct, so the occurrence index needs no filter | Does a UNIQUE index treat two NULLs as EQUAL? **SQL Server does**, and every ordinary row has a null `ParentTaskId`, so the `(ParentTaskId, ScheduledExecutionUtc)` index must be FILTERED there (EF's convention adds it). Postgres/MySQL/SQLite treat them as distinct. |
| **Check-constraint identifier quoting** | folds unquoted identifiers to lowercase, so the body must QUOTE the columns | `CK_QueuedTasks_OccurrenceSlot` is written once in the shared model. Override `TaskStoreEfDbContext.OccurrenceSlotCheckSql` when the DB cannot resolve unquoted mixed-case column names. |
| **Self-referencing FK on an existing table** | `ALTER TABLE ... ADD FOREIGN KEY` works | Can the FK and the check constraint be added to an existing table? SQLite cannot, so EF rebuilds the table in the migration. The FK is **Restrict**, never cascade, and `Remove(schedule)` deletes the occurrences in the SAME transaction. |
| **Durable-occurrence mechanism** | writable CTE whose `decision` branch RETURNS the outcome | Same answer as the hot-write row, applied to `MaterializeOccurrence` / `CancelSchedule` / the compare-and-swap advances. Can the mechanism return a value? Postgres: a scalar from the CTE. SQL Server: an OUTPUT parameter. MySQL: an OUT parameter, which needs `CommandType.StoredProcedure` — `ExecuteSqlRaw` cannot set it, so call through ADO. |
| **Column type mapping** | uuid/timestamptz/text/varchar(n)/boolean/bigint IDENTITY | Confirm the scaffolded types are sane; the model uses only `HasMaxLength`/`HasConversion<string>` (portable) — check no `HasColumnType` surprises. |
| **Testcontainers module + Respawn adapter** | `Testcontainers.PostgreSql` + `DbAdapter.Postgres` (+ `SchemasToInclude`) | Which Testcontainers module + `Respawn.DbAdapter`? Schema/db inclusion rules for Respawn. |

If ≥2 axes diverge from Postgres, expect a hybrid: **SQLite override pattern** for the untranslatable
LINQ + **SqlServer stored-proc pattern** for Phase-2.

## STEP 1 — Phase 1: thin provider + GATE

Mirror `EverTask.Storage.Postgres/` (or `.Sqlite/` if many axes diverge). Files:
`*.csproj`, `GlobalUsings.cs`, `XTaskStoreOptions.cs` (set `SchemaName` per the matrix), `XTaskStoreContext.cs`,
`DbContextFactoryAdapter.cs`, `ServiceCollectionExtensions.cs` (`AddXStorage`, `UseX(...)`, GUID generator,
`MigrationsHistoryTable(name, schema)` if schemas exist), `XTaskStorage.cs` (empty if zero overrides; else
override ONLY the methods the matrix flagged), `TaskStoreEfDbContextFactory.cs` (`#if DEBUG`), `AGENTS.md`,
and (if runtime schema needed) a copied `DbSchemaAwareMigrationAssembly.cs` + hand-edited `Initial`.

Then wire: `Directory.Packages.props` (per-TFM provider version), `*.slnx`, the test `.csproj`.

`QueuedTasks` carries three durable-occurrence columns on top of the ordinary ones: `ParentTaskId`
(nullable id), `RuntimeInfo` (nullable text) and `ScheduleVersion` (int, default 0), plus the restrict
self-referencing FK, the unique index `UX_QueuedTasks_Occurrence` on `(ParentTaskId, ScheduledExecutionUtc)`
and the check constraint `CK_QueuedTasks_OccurrenceSlot`. They all come from the shared model, so the
generated migration already contains them — check the matrix rows above for what the DB needs done
differently (index filter, quoted check body, table rebuild).

Generate the migration with `dotnet ef migrations add Initial` (DEBUG factory), inspect the types/recovery
index, hand-edit for schema if Option B, then **the GATE** — do NOT call Phase 1 done until all pass:

1. `dotnet build *.slnx -c Release` → **0 warnings** (TreatWarningsAsErrors) on net8/9/10.
2. Write `XEfCoreTaskStorageTests` mirroring `SqlServerEfCoreTaskStorageTests` (Testcontainers module pinned
   to a small image, Respawn adapter, `GetGuidForProvider`, schema assertion). Run the **full inherited
   `EfCoreTaskStorageTestsBase`** green on a real container.
3. **Capture the SQL** of `RetrievePending` and one `Cleanup*` (`.ToQueryString()`/logging). Confirm the
   keyset and the delete run **server-side** (no client-eval). If a construct throws / client-evals → add the
   minimal override (SQLite pattern) and re-run. Document every override + why.
4. Cross-provider tests added in the base suite still pass on the existing providers (no regressions).

## STEP 2 — Phase 2 (optional, perf): hot-write optimization

Only if profiling justifies it; the base is already atomic/correct. Override `SetStatus`,
`UpdateCurrentRun`, `CompleteRecurringRun` using the mechanism from the matrix (writable CTE OR stored
procedure OR PL/SQL). **Invariants that MUST hold (verify with tests):**

- **Audit parity with `AuditPolicy`** — `SetStatus`: gate from the INPUT status/exception (C# bool is fine).
  `UpdateCurrentRun`: the `ErrorsOnly` RunsAudit gate depends on the **row's** Status/Exception → decide it
  **server-side** (never a single C# bool); the audited values are the pre-update row's (a non-mutating
  `RETURNING`/`SELECT` is faithful). `CompleteRecurringRun`: audits CONSTANTS (`Completed`/null) → gate is
  C#-computable from the level (StatusAudit at Full; RunsAudit at Full+Minimal).
- **Propagation contracts** — `SetStatus` swallows; `UpdateCurrentRun`/`CompleteRecurringRun` **rethrow**
  (Residual D: never advance the schedule on unpersisted state).
- **Counter overflow** — `+1` on the `integer` run counter must raise the DB's out-of-range error and roll
  the statement back atomically (Postgres: SQLSTATE 22003). Re-add the overflow-propagation test.
- **Atomicity** — one statement / one transaction: audit insert + row update commit together; a forced
  mid-statement failure persists nothing.
- **NextRunUtc assigned unconditionally** in the recurring completion (a null makes the series terminal).

### Durable occurrences: not optional if you advertise them

`ITaskStorage` exposes the durable-occurrence operations as default members that **throw
`NotSupportedException`**, plus two capabilities that default to `false`:

```csharp
bool SupportsDurableOccurrences => false;   // MaterializeOccurrence, TryAdvanceScheduleCursor,
                                            // CancelSchedule, RequeueTerminal,
                                            // TryRequeueStaleOccurrence, TryHaltSchedule,
                                            // TrySetRecurringSeriesCompleted
bool SupportsScheduleVersioning => false;   // UpdateSchedule + the CAS overloads of
                                            // UpdateCurrentRun / CompleteRecurringRun
```

Inheriting `EfCoreTaskStorage` turns both on for a **relational** provider, because the base implements every
one of them as a conditional UPDATE inside a transaction. It answers both flags from the provider itself, so a
non-relational EF one (InMemory) gets `false` rather than a promise it would break at the first
materialization — which is the shape of the rule below. Capability and implementation are
inseparable: never flip a flag without a real atomic implementation, and never "almost" implement one with
two separate writes, which is exactly the crash window they exist to close.

The two operations that run once per occurrence — `MaterializeOccurrence` and the CAS advances — belong at
the same optimization tier as the three hot writes, so override them with the mechanism from the matrix
(SQL Server / MySQL: procedures; Postgres: writable CTEs). The rarer administrative ones (requeue, halt,
reschedule, conditional finalize) stay on the base, like the other once-per-series writes already do.

Whichever mechanism you pick, `MaterializeOccurrence` decides its outcome in THIS order — get it wrong and
the contract suite still passes while your provider silently disagrees with the other four:

1. row gone, `Cancelled`, or `NextRunUtc IS NULL` → `ParentInactive` (a finished or poisoned series must
   never grow one more occurrence);
2. version differs → `VersionMismatch`;
3. cursor differs from the expected one — **a NULL expected cursor included**, since a live schedule always
   has one → `CursorMoved`. This is the trap: written as a plain `NextRunUtc = @cursor`, a null parameter is
   rewritten to `IS NULL` and matches exactly the rows step 1 excludes, so the caller that retried with the
   cursor it read back resurrects the finished series;
4. `(ParentTaskId, ScheduledExecutionUtc)` already taken → `AlreadyExists`;
5. otherwise insert the occurrence, advance the cursor and — when the new cursor is null — finalize the
   schedule, all in the same commit.

The inserted row has ONE shape on every backend: `QueuedTask.ApplyOccurrenceContract(scheduleId,
scheduleVersion)`. A fresh one-shot at the version it was materialized against, `WaitingQueue`, run count 0,
with the definition, the cursor, the bounds and the task key cleared; the caller's id, creation time, slot,
type, payload, handler, queue, audit level and runtime info survive. Spell exactly those columns in your
`INSERT`, and call the method on the entity as well so the object the caller goes on using matches the row
you stored. A provider that persists the entity verbatim instead stores a materially different row — a
different `ScheduleVersion` for the same call, and whatever schedule-only fields the entity happened to
carry.

`TryAdvanceScheduleCursor` is the ONE write a skipped slot needs: a conditional UPDATE of `NextRunUtc`
guarded by version + cursor, with no run counted and no audit written, because nothing executed. Every slot
that survives the misfire policy carries the cursor forward inside `MaterializeOccurrence` instead — the
occurrence is written at the slot that survives while the cursor jumps over the ones that did not — so this
is only reached when nothing survives at all. A cursor that would move to `null` goes through
`TrySetRecurringSeriesCompleted`, which is why the new cursor here is not nullable. One statement, so the
base's version is already at the right tier: leave it alone.

`CancelSchedule` cancels exactly the set startup recovery would put back in a queue: `WaitingQueue`,
`Queued`, `Pending` and `ServiceStopped`. Leave one of those out and an occurrence of a cancelled schedule
comes back at the next restart and runs. Occurrences already `InProgress` own a live delivery and are left
alone.

It audits only the rows its UPDATE really changed, the schedule row included: cancelling a
schedule a concurrent `Remove` already deleted is a silent no-op everywhere, and an audit row for a task
that no longer exists violates the `StatusAudit` foreign key and takes the whole call down. "Really changed"
is not the id list a preceding SELECT returned — an occurrence that reached `InProgress` in between is
skipped by the conditional UPDATE and must not get a `Cancelled` audit row for a status it never took.
**If your engine admits concurrent writers, read that set back from the UPDATE itself** (`OUTPUT`,
`RETURNING`): under READ COMMITTED a second read can attribute to this call an occurrence another writer
cancelled, so the trail would claim a transition your transaction never made. Re-reading the candidates
inside the same transaction — what the EF base does — is exact only while writers are serialized, as they
are on SQLite.

**Verify how your engine treats an error inside a multi-statement procedure.** SQL Server, by default, aborts
only the failing statement: the procedure runs on to the writes that follow and COMMITs half the operation,
so every procedure that owns a transaction needs `SET XACT_ABORT ON`. MySQL/MariaDB need an
`EXIT HANDLER FOR SQLEXCEPTION` that rolls back and re-signals. A single-statement mechanism (a Postgres
writable CTE) is atomic for free. Pin it with a fault-injection test — give the occurrence the primary key of
an existing row and assert the cursor did not move; nothing else surfaces this.

Also override `CleanupTerminalOccurrences(cutoff, preserveTasksWithLogs, ct)` if the DB cannot translate the
`DateTimeOffset` age cutoff (the SQLite pattern) — or if it cannot be trusted with a correlated `EXISTS`
inside a `DELETE … LIMIT`, which is the MySQL trap: the guard is silently dropped and every occurrence is
purged, cascade-deleting the `TaskExecutionLog` rows the log window kept. It shares that `preserveTasksWithLogs`
guard with `CleanupCompletedTasks`, so whichever shape you pick, pick it for both.

## STEP 3 — Packaging & docs checklist (do NOT skip — "in every form")

- `Directory.Packages.props`: provider version in EACH per-TFM ItemGroup + `Testcontainers.X` in the test group.
- `*.slnx`: add the project; test `.csproj`: ProjectReference + Testcontainers PackageReference.
- `.github/workflows/release.yml`: add the `dotnet pack src/Storage/EverTask.Storage.X/...` line (else it
  never ships to NuGet). `build.yml` builds the slnx automatically; the new tests run on CI if the image is
  small (don't exclude them unless the image is heavy like mssql).
- `README.md`: NuGet badge, package list, persistence/storage enumerations, install snippet, roadmap.
- `docs/`: new `docs/storage/x-storage.md` (mirror `sql-server-storage.md` front-matter + nav_order, bump
  siblings), and add the provider to EVERY enumeration: `storage/overview.md` (comparison table + section +
  "when to use" + links), `storage.md`, `index.md`, `getting-started.md`, `configuration-cheatsheet.md`
  (method table + `SchemaName`), `configuration-reference.md` (`AddXStorage` API), and remove the DB from any
  "implement custom storage for X" list (`storage/custom-storage.md`).
- Root `AGENTS.md` (Key Features, Solution Structure, module table) + `EfCore/AGENTS.md` ("future …") +
  the new provider `AGENTS.md` (operational gotchas only) + `CHANGELOG.md` (under `[Unreleased]`).
- Run `/humanizer` on the public `.md` YOU authored (new page, CHANGELOG, README/docs prose) — surgical:
  remove AI tells (promotional language, copula avoidance, em-dash/significance inflation), do NOT inject
  first-person "personality" into reference docs (it clashes with the sibling pages' house style).

## Invariants that must never break (all providers)

- **The recovery filter returns TWO categories**, and its expression is duplicated in `EfCoreTaskStorage`,
  `SqliteTaskStorage` (override), `MemoryTaskStorage` and the Postgres partial index — keep all in sync
  (covered by `EfCoreTaskStorageTestsBase`). Rows to EXECUTE come from
  `QueuedTask.IsRecoverableForExecution(now)` (note the grouped temporal term: a series whose `RunUntil`
  elapsed during a downtime keeps the occurrence it had already scheduled before that boundary); series to
  FINALIZE come from `QueuedTask.IsRecurringSeriesToFinalize()`. `RetrievePending` returns their union;
  `TrySetQueuedIfRecoverable` applies only the first.
- **An untranslatable predicate moves the CONDITION, never the check-and-set.** If the DB cannot translate
  part of the recoverable predicate, evaluate that part in memory on a row read first — but the write must
  still be a conditional UPDATE, with the translatable half (`EfCoreTaskStorage.RecoverableStatusAndBudget`)
  and a by-value re-assertion of the columns the in-memory half was decided from in its WHERE clause. A
  tracked read-then-`SaveChanges` is a read-then-write: a `Cancel` that linearizes in between is overwritten
  with `Queued` and the cancelled task runs at the next restart. SQLite is the worked example
  (`SqliteTaskStorage.TrySetQueuedIfRecoverable`), and it also shows the two traps — re-assert the temporal
  columns VERBATIM (a store that keeps a `DateTimeOffset` as text compares the text), and read OUTSIDE the
  write transaction if the engine locks on `BEGIN`.
- **The clock travels with the call**: the core always uses the `nowUtc` overloads of `RetrievePending` and
  `TrySetQueuedIfRecoverable`, so the storage never resolves "now" itself. Override them, or the provider
  silently opts out of the deterministic scheduling clock. Overriding only the older four/three-argument
  signatures still works — `EfCoreTaskStorage` detects that and hands the clock-carrying calls back to them —
  but then the clock is the real one, not the injected `TimeProvider`.
- **Finalizing a series is conditional where the storage can be**: with `SupportsScheduleVersioning` the
  recovery uses `TrySetRecurringSeriesCompleted`, compare-and-swapped on the cursor, status and version the
  decision was computed from, so a `Cancel` that linearized in between wins. Without the capability the
  historical unconditional `SetRecurringSeriesCompleted` stands — never let the CAS member's
  `NotSupportedException` reach the recovery, which counts it as a failure and poisons the row.
- **`IGuidGenerator`** picks a DB-appropriate UUIDNext layout so PK order matches insert order (recovery index).
- **No silent coverage gaps**: if a base test can't run on the DB, say so; if you add an override, document
  the reason. Never let "tests pass" hide a skipped axis.

## Recommended flow for a hard/divergent target (e.g. MySQL)

Treat it like the Postgres effort: (1) write a file-by-file plan + decision matrix (mirror
`review/postgres-provider-*.md`), (2) optionally run an adversarial review of the plan (see the
`adversarial-review` skill + Codex as a second opinion), (3) implement Phase 1 to the GATE, (4) decide
Phase 2, (5) packaging/docs, (6) humanize. Expect for MySQL: SQLite-style overrides for DateTimeOffset,
stored procedures (not CTEs) for Phase 2, and `SchemaName` semantics closer to "no schema".
