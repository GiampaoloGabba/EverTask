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
| `src/Storage/EverTask.Storage.EfCore/EfCoreTaskStorage.cs` + its `CLAUDE.md` | The base + the "New EF Core Provider" checklist + which methods are `virtual`. |
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
| **Column type mapping** | uuid/timestamptz/text/varchar(n)/boolean/bigint IDENTITY | Confirm the scaffolded types are sane; the model uses only `HasMaxLength`/`HasConversion<string>` (portable) — check no `HasColumnType` surprises. |
| **Testcontainers module + Respawn adapter** | `Testcontainers.PostgreSql` + `DbAdapter.Postgres` (+ `SchemasToInclude`) | Which Testcontainers module + `Respawn.DbAdapter`? Schema/db inclusion rules for Respawn. |

If ≥2 axes diverge from Postgres, expect a hybrid: **SQLite override pattern** for the untranslatable
LINQ + **SqlServer stored-proc pattern** for Phase-2.

## STEP 1 — Phase 1: thin provider + GATE

Mirror `EverTask.Storage.Postgres/` (or `.Sqlite/` if many axes diverge). Files:
`*.csproj`, `GlobalUsings.cs`, `XTaskStoreOptions.cs` (set `SchemaName` per the matrix), `XTaskStoreContext.cs`,
`DbContextFactoryAdapter.cs`, `ServiceCollectionExtensions.cs` (`AddXStorage`, `UseX(...)`, GUID generator,
`MigrationsHistoryTable(name, schema)` if schemas exist), `XTaskStorage.cs` (empty if zero overrides; else
override ONLY the methods the matrix flagged), `TaskStoreEfDbContextFactory.cs` (`#if DEBUG`), `CLAUDE.md`,
and (if runtime schema needed) a copied `DbSchemaAwareMigrationAssembly.cs` + hand-edited `Initial`.

Then wire: `Directory.Packages.props` (per-TFM provider version), `*.slnx`, the test `.csproj`.

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
- Root `CLAUDE.md` (Key Features, Solution Structure, module table) + `EfCore/CLAUDE.md` ("future …") +
  the new provider `CLAUDE.md` (operational gotchas only) + `CHANGELOG.md` (under `[Unreleased]`).
- Run `/humanizer` on the public `.md` YOU authored (new page, CHANGELOG, README/docs prose) — surgical:
  remove AI tells (promotional language, copula avoidance, em-dash/significance inflation), do NOT inject
  first-person "personality" into reference docs (it clashes with the sibling pages' house style).

## Invariants that must never break (all providers)

- **`RetrievePending` recoverable-status filter** is duplicated in `EfCoreTaskStorage`, `SqliteTaskStorage`
  (override), and `MemoryTaskStorage` — keep all in sync (covered by `EfCoreTaskStorageTestsBase`).
- **`IGuidGenerator`** picks a DB-appropriate UUIDNext layout so PK order matches insert order (recovery index).
- **No silent coverage gaps**: if a base test can't run on the DB, say so; if you add an override, document
  the reason. Never let "tests pass" hide a skipped axis.

## Recommended flow for a hard/divergent target (e.g. MySQL)

Treat it like the Postgres effort: (1) write a file-by-file plan + decision matrix (mirror
`review/postgres-provider-*.md`), (2) optionally run an adversarial review of the plan (see the
`adversarial-review` skill + Codex as a second opinion), (3) implement Phase 1 to the GATE, (4) decide
Phase 2, (5) packaging/docs, (6) humanize. Expect for MySQL: SQLite-style overrides for DateTimeOffset,
stored procedures (not CTEs) for Phase 2, and `SchemaName` semantics closer to "no schema".
