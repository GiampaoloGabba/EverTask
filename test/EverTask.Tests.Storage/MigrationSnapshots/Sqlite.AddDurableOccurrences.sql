BEGIN TRANSACTION;
ALTER TABLE "QueuedTasks" ADD "ParentTaskId" TEXT NULL;

ALTER TABLE "QueuedTasks" ADD "RuntimeInfo" TEXT NULL;

ALTER TABLE "QueuedTasks" ADD "ScheduleVersion" INTEGER NOT NULL DEFAULT 0;

CREATE INDEX "IX_QueuedTasks_ParentTaskId" ON "QueuedTasks" ("ParentTaskId");

CREATE UNIQUE INDEX "UX_QueuedTasks_Occurrence" ON "QueuedTasks" ("ParentTaskId", "ScheduledExecutionUtc");

CREATE TABLE "ef_temp_QueuedTasks" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_QueuedTasks" PRIMARY KEY,
    "AuditLevel" INTEGER NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "CurrentRunCount" INTEGER NULL,
    "Exception" TEXT NULL,
    "ExecutionTimeMs" REAL NOT NULL,
    "Handler" TEXT NOT NULL,
    "IsRecurring" INTEGER NOT NULL,
    "LastExecutionUtc" TEXT NULL,
    "MaxRuns" INTEGER NULL,
    "NextRunUtc" TEXT NULL,
    "ParentTaskId" TEXT NULL,
    "QueueName" TEXT NULL,
    "RecoveryDispatchFailureCount" INTEGER NULL,
    "RecurringInfo" TEXT NULL,
    "RecurringTask" TEXT NULL,
    "Request" TEXT NOT NULL,
    "RunUntil" TEXT NULL,
    "RuntimeInfo" TEXT NULL,
    "ScheduleVersion" INTEGER NOT NULL DEFAULT 0,
    "ScheduledExecutionUtc" TEXT NULL,
    "Status" TEXT NOT NULL,
    "TaskKey" TEXT NULL,
    "Type" TEXT NOT NULL,
    CONSTRAINT "CK_QueuedTasks_OccurrenceSlot" CHECK (ParentTaskId IS NULL OR ScheduledExecutionUtc IS NOT NULL),
    CONSTRAINT "FK_QueuedTasks_QueuedTasks_ParentTaskId" FOREIGN KEY ("ParentTaskId") REFERENCES "QueuedTasks" ("Id") ON DELETE RESTRICT
);

INSERT INTO "ef_temp_QueuedTasks" ("Id", "AuditLevel", "CreatedAtUtc", "CurrentRunCount", "Exception", "ExecutionTimeMs", "Handler", "IsRecurring", "LastExecutionUtc", "MaxRuns", "NextRunUtc", "ParentTaskId", "QueueName", "RecoveryDispatchFailureCount", "RecurringInfo", "RecurringTask", "Request", "RunUntil", "RuntimeInfo", "ScheduleVersion", "ScheduledExecutionUtc", "Status", "TaskKey", "Type")
SELECT "Id", "AuditLevel", "CreatedAtUtc", "CurrentRunCount", "Exception", "ExecutionTimeMs", "Handler", "IsRecurring", "LastExecutionUtc", "MaxRuns", "NextRunUtc", "ParentTaskId", "QueueName", "RecoveryDispatchFailureCount", "RecurringInfo", "RecurringTask", "Request", "RunUntil", "RuntimeInfo", "ScheduleVersion", "ScheduledExecutionUtc", "Status", "TaskKey", "Type"
FROM "QueuedTasks";

COMMIT;

PRAGMA foreign_keys = 0;

BEGIN TRANSACTION;
DROP TABLE "QueuedTasks";

ALTER TABLE "ef_temp_QueuedTasks" RENAME TO "QueuedTasks";

COMMIT;

PRAGMA foreign_keys = 1;

BEGIN TRANSACTION;
CREATE INDEX "IX_QueuedTasks_ParentTaskId" ON "QueuedTasks" ("ParentTaskId");

CREATE INDEX "IX_QueuedTasks_Status" ON "QueuedTasks" ("Status");

CREATE UNIQUE INDEX "IX_QueuedTasks_TaskKey" ON "QueuedTasks" ("TaskKey");

CREATE UNIQUE INDEX "UX_QueuedTasks_Occurrence" ON "QueuedTasks" ("ParentTaskId", "ScheduledExecutionUtc");

COMMIT;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260822183444_AddDurableOccurrences', '<EFCORE-VERSION>');