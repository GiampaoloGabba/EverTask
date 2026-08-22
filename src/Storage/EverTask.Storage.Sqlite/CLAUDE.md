# EverTask.Storage.Sqlite

Refer to the root CLAUDE.md for project-wide rules.

- **`SchemaName` MUST stay `""`** — SQLite has no schema concept. Unlike `AddMySqlStorage`, `AddSqliteStorage`
  does NOT throw on a non-empty value: it is accepted silently and breaks later.
- SQLite cannot translate `DateTimeOffset` ordering comparisons (`<`/`>`/`OrderBy`). The optimized server-side
  query always stays in the `EfCoreTaskStorage` base; `SqliteTaskStorage` overrides only the methods that hit
  this limit and evaluates them client-side (resolve ids, then delete/update by key). **Never push a SQLite
  workaround down into the base** — add an override here instead.
- The 9 overrides: `RetrievePending`, `TrySetQueuedIfRecoverable`, the retention cleanup
  (`CleanupStatusAudits`, `CleanupRunsAudits`, `CleanupExecutionLogsByAge`, `CleanupExecutionLogsByCount`,
  `CleanupCompletedTasks`) and the date-filtered statistics (`CountByStatusAsync`, `CountByQueueAndStatusAsync`
  — only when a `createdAtOrAfterUtc` filter is supplied; the unfiltered path reuses the base `GROUP BY`).
- `RetrievePending` inlines its own copy of the recoverable-status list — keep it in sync with the other three
  copies listed in `../EverTask.Storage.EfCore/CLAUDE.md`.
