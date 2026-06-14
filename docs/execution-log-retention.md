---
layout: default
title: Execution Log Retention (TODO)
published: false
---

# Execution Log Retention — TODO

Status: **not implemented**. This note records a known gap and a proposed design so it can be picked up later.

## Current state

Captured execution logs (`TaskExecutionLog`, written when `PersistentLogger` is enabled) have **no retention of their own**. `AuditRetentionPolicy` governs only `StatusAudit` and `RunsAudit`.

Execution logs are removed in exactly one case: when their parent `QueuedTask` is deleted, the database cascade (`OnDelete(Cascade)`) takes them with it. The only thing that deletes a completed task today is the audit cleanup, and only when:

- `DeleteCompletedTasksAfterRetention` is enabled (opt-in, default off), and
- the task is older than the longest configured audit-retention window, and
- the task has no remaining `StatusAudit` / `RunsAudit` rows.

So if you delete a task, it takes everything it owns — audits and logs. That is the intended behavior.

## The gap

There is no way to:

- keep execution logs **longer** than the task itself, or
- purge old execution logs **independently** of the task (for example, trim logs while keeping the task row and its audits, or trim logs of tasks that still have audits or have not reached the audit cutoff).

A long-running service that persists logs but keeps audits (or doesn't enable completed-task deletion) accumulates execution logs without bound.

## Proposed design

1. Add `int? ExecutionLogRetentionDays` to `AuditRetentionPolicy` (nullable, default `null` = unlimited, so current behavior is unchanged).
2. Add a cleanup pass in `AuditCleanupHostedService` that deletes `TaskExecutionLog` rows older than `now - ExecutionLogRetentionDays` (anchored on `TimestampUtc`), in batches, independent of the parent task — mirroring `CleanupStatusAudit` / `CleanupRunsAudit`.
3. SQLite cannot translate `DateTimeOffset` comparisons server-side; resolve the ids to delete client-side (as `SqliteTaskStorage.RetrievePending` and the completed-task cleanup already do) and delete by id.
4. Once logs are trimmed, an aged-out task with neither audits nor logs becomes eligible for the existing completed-task deletion, so the two passes compose.

## Tests (when implemented)

- Cross-provider in `EfCoreTaskStorageTestsBase` (SQLite + real SQL Server): logs past the cutoff are deleted, recent logs survive, `null` retention deletes nothing.
- No regression in the completed-task age gate.

## Notes

- No SQL Server migration is required for a delete-only pass (no schema change), unless a stored procedure is preferred for consistency with the other cleanups.
- Keep the default `null` so enabling persistent logging never silently starts deleting logs.
