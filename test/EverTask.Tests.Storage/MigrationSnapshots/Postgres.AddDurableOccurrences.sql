START TRANSACTION;
ALTER TABLE evertask."QueuedTasks" ADD "ParentTaskId" uuid;

ALTER TABLE evertask."QueuedTasks" ADD "RuntimeInfo" text;

ALTER TABLE evertask."QueuedTasks" ADD "ScheduleVersion" integer NOT NULL DEFAULT 0;

CREATE INDEX "IX_QueuedTasks_ParentTaskId" ON evertask."QueuedTasks" ("ParentTaskId");

CREATE UNIQUE INDEX "UX_QueuedTasks_Occurrence" ON evertask."QueuedTasks" ("ParentTaskId", "ScheduledExecutionUtc");

ALTER TABLE evertask."QueuedTasks" ADD CONSTRAINT "CK_QueuedTasks_OccurrenceSlot" CHECK ("ParentTaskId" IS NULL OR "ScheduledExecutionUtc" IS NOT NULL);

ALTER TABLE evertask."QueuedTasks" ADD CONSTRAINT "FK_QueuedTasks_QueuedTasks_ParentTaskId" FOREIGN KEY ("ParentTaskId") REFERENCES evertask."QueuedTasks" ("Id") ON DELETE RESTRICT;

INSERT INTO evertask."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260822182810_AddDurableOccurrences', '<EFCORE-VERSION>');

COMMIT;