# EverTask.Storage.Sqlite

Refer to the root CLAUDE.md for project-wide rules.

- **`SchemaName` MUST stay `""`** — SQLite has no schema concept. Unlike `AddMySqlStorage`, `AddSqliteStorage`
  does NOT throw on a non-empty value: it is accepted silently and breaks later.
- SQLite cannot translate `DateTimeOffset` ordering comparisons (`<`/`>`/`OrderBy`). The optimized server-side
  query always stays in the `EfCoreTaskStorage` base; `SqliteTaskStorage` overrides only the methods that hit
  this limit and evaluates them client-side (resolve ids, then delete/update by key). **Never push a SQLite
  workaround down into the base** — add an override here instead.
- **Client-side is the workaround, not the licence.** It is right where the set moved into memory is the one
  the caller was going to get anyway (a recovery page, the ids of a cleanup batch). Where the whole point of
  the member is that the set stays in the store, write the query as SQL instead: `GetOccurrencesPage` orders
  and slices through `FromSql` (interpolated, so every value is still a parameter EF types), because reading
  a whole series to hand back a hundred rows is the pathology it exists to prevent. SQLite keeps a
  `DateTimeOffset` as ISO-8601 text with a fixed date-and-time prefix, so plain text ordering IS slot
  ordering — the same representational equality `UX_QueuedTasks_Occurrence` already rests on (follow-up F1).
  Pinned on all four providers by
  `EfCoreTaskStorageTestsBase.GetOccurrencesPage_should_let_the_database_order_and_slice_the_series`, which
  reads the command off the EF diagnostic source: asserting the rows alone passes on an in-memory slice.
- The 9 overrides: `RetrievePending`, `TrySetQueuedIfRecoverable`, the retention cleanup
  (`CleanupStatusAudits`, `CleanupRunsAudits`, `CleanupExecutionLogsByAge`, `CleanupExecutionLogsByCount`,
  `CleanupCompletedTasks`) and the date-filtered statistics (`CountByStatusAsync`, `CountByQueueAndStatusAsync`
  — only when a `createdAtOrAfterUtc` filter is supplied; the unfiltered path reuses the base `GROUP BY`).
- The overrides are now **12**: the nine above plus the `nowUtc` overloads of `RetrievePending` /
  `TrySetQueuedIfRecoverable` and `CleanupTerminalOccurrences`. BOTH arities of the two recovery members are
  overridden on purpose — the legacy one delegates to the `nowUtc` one here, and the base only hands a
  clock-carrying call back to a legacy override when the `nowUtc` one is NOT overridden. The
  durable-occurrence operations inherit the base unchanged — each is a conditional UPDATE inside a
  transaction, which SQLite executes atomically like any other write.
- **`TrySetQueuedIfRecoverable` moves ONLY the temporal term in memory, never the whole check.** The write
  stays a conditional UPDATE: its WHERE carries the base's shared `RecoverableStatusAndBudget` plus a
  by-value re-assertion of `NextRunUtc`/`RunUntil`, taken verbatim from the row that was read — never
  normalized to UTC, because SQLite compares the stored TEXT. A tracked read-then-`SaveChanges` is not a
  check-and-set: a `Cancel` landing in between was overwritten with `Queued`, and the cancelled task ran at
  the next restart (`test/EverTask.Tests.Storage/SqliteRecoveryTransitionCasTests.cs`). The read runs
  OUTSIDE the transaction — Microsoft.Data.Sqlite issues `BEGIN IMMEDIATE`, so the write lock is taken the
  moment it opens and a reader inside it would lock the racing writer out instead of losing to it.
- SQLite cannot ALTER a table to add a foreign key or a check constraint, so the `AddDurableOccurrences`
  migration **rebuilds the table** (EF generates the rebuild; just know it when reading the SQL). The rebuilt
  columns come from the MODEL, not from the `AddColumn` operations: a column default declared only in the
  migration is thrown away here, which is why `ScheduleVersion` carries `HasDefaultValue(0)` in
  `TaskStoreEfDbContext`. Pinned by `MigrationSnapshots/Sqlite.AddDurableOccurrences.sql` and by a catalog
  assertion on `pragma_table_info`.
- `GetOccurrencesPage` inlines its own copy of the base's `NonTerminalOccurrence` set, twice — once in LINQ
  for the count, once in the SQL of the page. Change the base's and change both of these.
- `RetrievePending` inlines its own copy of the recoverable-status list — keep it in sync with the other three
  copies listed in `../EverTask.Storage.EfCore/CLAUDE.md`. Only the STATUS set is pushed down: the `MaxRuns`
  gate stays client-side too, because a series to finalize is precisely a row whose budget is spent and a
  server-side prefilter would drop exactly the rows that page must return.
