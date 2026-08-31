BEGIN TRANSACTION;
ALTER TABLE [EverTask].[QueuedTasks] ADD [ParentTaskId] uniqueidentifier NULL;

ALTER TABLE [EverTask].[QueuedTasks] ADD [RuntimeInfo] nvarchar(max) NULL;

ALTER TABLE [EverTask].[QueuedTasks] ADD [ScheduleVersion] int NOT NULL DEFAULT 0;

CREATE INDEX [IX_QueuedTasks_ParentTaskId] ON [EverTask].[QueuedTasks] ([ParentTaskId]);

CREATE INDEX [IX_QueuedTasks_ParentTaskId_Status] ON [EverTask].[QueuedTasks] ([ParentTaskId], [Status]);

CREATE UNIQUE INDEX [UX_QueuedTasks_Occurrence] ON [EverTask].[QueuedTasks] ([ParentTaskId], [ScheduledExecutionUtc]) WHERE [ParentTaskId] IS NOT NULL AND [ScheduledExecutionUtc] IS NOT NULL;

ALTER TABLE [EverTask].[QueuedTasks] ADD CONSTRAINT [CK_QueuedTasks_OccurrenceSlot] CHECK (ParentTaskId IS NULL OR ScheduledExecutionUtc IS NOT NULL);

ALTER TABLE [EverTask].[QueuedTasks] ADD CONSTRAINT [FK_QueuedTasks_QueuedTasks_ParentTaskId] FOREIGN KEY ([ParentTaskId]) REFERENCES [EverTask].[QueuedTasks] ([Id]) ON DELETE NO ACTION;

DROP PROCEDURE IF EXISTS [EverTask].[usp_MaterializeOccurrence]

CREATE PROCEDURE [EverTask].[usp_MaterializeOccurrence]
  @ParentId UNIQUEIDENTIFIER,
  @ExpectedScheduleVersion INT,
  @ExpectedCursorUtc DATETIMEOFFSET,
  @NewCursorUtc DATETIMEOFFSET = NULL,
  @AuditLevel INT = 0,
  @OccurrenceId UNIQUEIDENTIFIER,
  @SlotUtc DATETIMEOFFSET,
  @CreatedAtUtc DATETIMEOFFSET,
  @Type NVARCHAR(500),
  @Request NVARCHAR(MAX),
  @Handler NVARCHAR(500),
  @QueueName NVARCHAR(MAX) = NULL,
  @OccurrenceAuditLevel INT = NULL,
  @RuntimeInfo NVARCHAR(MAX) = NULL,
  @Outcome INT OUTPUT
AS
BEGIN
  SET NOCOUNT ON;
  -- Any run-time error must undo the WHOLE transaction, not just the statement that raised it. With
  -- XACT_ABORT OFF a constraint violation aborts only its own statement, the procedure runs on to the
  -- writes that follow and COMMITs half the operation -- exactly the partial state these
  -- single-transaction procedures exist to make impossible.
  SET XACT_ABORT ON;

  DECLARE @Now DATETIMEOFFSET = SWITCHOFFSET(SYSDATETIMEOFFSET(), '+00:00');
  DECLARE @CurrentVersion INT, @CurrentCursor DATETIMEOFFSET, @CurrentStatus NVARCHAR(15);

  SET @Outcome = 0;

  BEGIN TRANSACTION;

  SELECT @CurrentVersion = ScheduleVersion, @CurrentCursor = NextRunUtc, @CurrentStatus = Status
  FROM [EverTask].[QueuedTasks] WITH (UPDLOCK, HOLDLOCK)
  WHERE Id = @ParentId;

  IF @@ROWCOUNT = 0 OR @CurrentStatus = 'Cancelled' OR @CurrentCursor IS NULL
  BEGIN
      SET @Outcome = 4;
      ROLLBACK TRANSACTION;
      RETURN;
  END

  IF @CurrentVersion <> @ExpectedScheduleVersion
  BEGIN
      SET @Outcome = 3;
      ROLLBACK TRANSACTION;
      RETURN;
  END

  IF @ExpectedCursorUtc IS NULL OR @CurrentCursor <> @ExpectedCursorUtc
  BEGIN
      SET @Outcome = 2;
      ROLLBACK TRANSACTION;
      RETURN;
  END

  IF EXISTS (SELECT 1 FROM [EverTask].[QueuedTasks]
             WHERE ParentTaskId = @ParentId AND ScheduledExecutionUtc = @SlotUtc)
  BEGIN
      SET @Outcome = 1;
      ROLLBACK TRANSACTION;
      RETURN;
  END

  INSERT INTO [EverTask].[QueuedTasks]
      (Id, CreatedAtUtc, ExecutionTimeMs, ScheduledExecutionUtc, Type, Request, Handler, IsRecurring,
       CurrentRunCount, QueueName, AuditLevel, Status, ParentTaskId, RuntimeInfo, ScheduleVersion)
  VALUES
      (@OccurrenceId, @CreatedAtUtc, 0, @SlotUtc, @Type, @Request, @Handler, 0,
       0, @QueueName, @OccurrenceAuditLevel, 'WaitingQueue', @ParentId, @RuntimeInfo, @ExpectedScheduleVersion);

  -- A null new cursor ENDS the series, in this same commit: a separate finalization would leave a window
  -- in which a crash resurrects a series that has already produced its last occurrence.
  UPDATE [EverTask].[QueuedTasks]
  SET NextRunUtc       = @NewCursorUtc,
      CurrentRunCount  = CASE WHEN ISNULL(CurrentRunCount, 0) >= 2147483647 THEN 2147483647 ELSE ISNULL(CurrentRunCount, 0) + 1 END,
      Status           = CASE WHEN @NewCursorUtc IS NULL THEN 'Completed' ELSE Status END,
      Exception        = CASE WHEN @NewCursorUtc IS NULL THEN NULL ELSE Exception END,
      LastExecutionUtc = CASE WHEN @NewCursorUtc IS NULL THEN @Now ELSE LastExecutionUtc END
  WHERE Id = @ParentId;

  IF @NewCursorUtc IS NULL AND (@AuditLevel = 0 OR @AuditLevel NOT IN (0, 1, 2, 3))
      INSERT INTO [EverTask].[StatusAudit] (QueuedTaskId, UpdatedAtUtc, NewStatus, Exception)
      VALUES (@ParentId, @Now, 'Completed', NULL);

  COMMIT TRANSACTION;
END

DROP PROCEDURE IF EXISTS [EverTask].[usp_CancelSchedule]

CREATE PROCEDURE [EverTask].[usp_CancelSchedule]
  @ParentId UNIQUEIDENTIFIER,
  @AuditLevel INT = 0
AS
BEGIN
  SET NOCOUNT ON;
  -- Any run-time error must undo the WHOLE transaction, not just the statement that raised it. With
  -- XACT_ABORT OFF a constraint violation aborts only its own statement, the procedure runs on to the
  -- writes that follow and COMMITs half the operation -- exactly the partial state these
  -- single-transaction procedures exist to make impossible.
  SET XACT_ABORT ON;

  DECLARE @Now DATETIMEOFFSET = SWITCHOFFSET(SYSDATETIMEOFFSET(), '+00:00');
  DECLARE @Cancelled TABLE (Id UNIQUEIDENTIFIER);
  DECLARE @ParentCancelled INT;

  BEGIN TRANSACTION;

  UPDATE [EverTask].[QueuedTasks]
  SET Status = 'Cancelled'
  WHERE Id = @ParentId;

  SET @ParentCancelled = @@ROWCOUNT;

  UPDATE [EverTask].[QueuedTasks]
  SET Status = 'Cancelled'
  OUTPUT inserted.Id INTO @Cancelled
  WHERE ParentTaskId = @ParentId AND Status IN ('WaitingQueue', 'Queued', 'Pending', 'ServiceStopped');

  -- The schedule row is audited only when the update actually found it: cancelling a schedule a concurrent
  -- Remove already deleted is a silent no-op on every other provider, while an audit row for a missing task
  -- violates the foreign key. Unknown audit levels fall back to Full.
  IF @AuditLevel = 0 OR @AuditLevel NOT IN (0, 1, 2, 3)
  BEGIN
      IF @ParentCancelled > 0
          INSERT INTO [EverTask].[StatusAudit] (QueuedTaskId, UpdatedAtUtc, NewStatus, Exception)
          VALUES (@ParentId, @Now, 'Cancelled', NULL);

      INSERT INTO [EverTask].[StatusAudit] (QueuedTaskId, UpdatedAtUtc, NewStatus, Exception)
      SELECT Id, @Now, 'Cancelled', NULL FROM @Cancelled;
  END

  COMMIT TRANSACTION;
END

DROP PROCEDURE IF EXISTS [EverTask].[usp_UpdateCurrentRunCas]

CREATE PROCEDURE [EverTask].[usp_UpdateCurrentRunCas]
  @TaskId UNIQUEIDENTIFIER,
  @ExecutionTimeMs FLOAT,
  @NextRunUtc DATETIMEOFFSET = NULL,
  @AuditLevel INT = 0,
  @ExpectedScheduleVersion INT,
  @Applied BIT OUTPUT
AS
BEGIN
  SET NOCOUNT ON;
  -- Any run-time error must undo the WHOLE transaction, not just the statement that raised it. With
  -- XACT_ABORT OFF a constraint violation aborts only its own statement, the procedure runs on to the
  -- writes that follow and COMMITs half the operation -- exactly the partial state these
  -- single-transaction procedures exist to make impossible.
  SET XACT_ABORT ON;

  DECLARE @Now DATETIMEOFFSET = SWITCHOFFSET(SYSDATETIMEOFFSET(), '+00:00');
  DECLARE @Status NVARCHAR(15);
  DECLARE @Exception NVARCHAR(MAX);
  DECLARE @ShouldAudit BIT = 0;

  SET @Applied = 0;

  BEGIN TRANSACTION;

  SELECT @Status = Status, @Exception = Exception
  FROM [EverTask].[QueuedTasks] WITH (UPDLOCK, HOLDLOCK)
  WHERE Id = @TaskId AND ScheduleVersion = @ExpectedScheduleVersion;

  IF @@ROWCOUNT = 0
  BEGIN
      ROLLBACK TRANSACTION;
      RETURN;
  END

  IF @AuditLevel IN (0, 1) OR @AuditLevel NOT IN (0, 1, 2, 3)
      SET @ShouldAudit = 1;
  ELSE IF @AuditLevel = 2 AND (@Status = 'Failed' OR (@Exception IS NOT NULL AND @Exception <> ''))
      SET @ShouldAudit = 1;

  UPDATE [EverTask].[QueuedTasks]
  SET ExecutionTimeMs = @ExecutionTimeMs,
      NextRunUtc = @NextRunUtc,
      CurrentRunCount = CASE WHEN ISNULL(CurrentRunCount, 0) >= 2147483647 THEN 2147483647 ELSE ISNULL(CurrentRunCount, 0) + 1 END
  WHERE Id = @TaskId;

  IF @ShouldAudit = 1
  BEGIN
      INSERT INTO [EverTask].[RunsAudit] (QueuedTaskId, ExecutedAt, ExecutionTimeMs, Status, Exception)
      VALUES (@TaskId, @Now, @ExecutionTimeMs, @Status, @Exception);
  END

  SET @Applied = 1;
  COMMIT TRANSACTION;
END

DROP PROCEDURE IF EXISTS [EverTask].[usp_CompleteRecurringRunCas]

CREATE PROCEDURE [EverTask].[usp_CompleteRecurringRunCas]
  @TaskId UNIQUEIDENTIFIER,
  @ExecutionTimeMs FLOAT,
  @NextRunUtc DATETIMEOFFSET = NULL,
  @AuditLevel INT = 0,
  @ExpectedScheduleVersion INT,
  @Applied BIT OUTPUT
AS
BEGIN
  SET NOCOUNT ON;
  -- Any run-time error must undo the WHOLE transaction, not just the statement that raised it. With
  -- XACT_ABORT OFF a constraint violation aborts only its own statement, the procedure runs on to the
  -- writes that follow and COMMITs half the operation -- exactly the partial state these
  -- single-transaction procedures exist to make impossible.
  SET XACT_ABORT ON;

  DECLARE @Now DATETIMEOFFSET = SWITCHOFFSET(SYSDATETIMEOFFSET(), '+00:00');
  DECLARE @ShouldStatusAudit BIT = CASE WHEN @AuditLevel = 0 OR @AuditLevel NOT IN (0,1,2,3) THEN 1 ELSE 0 END;
  DECLARE @ShouldRunsAudit   BIT = CASE WHEN @AuditLevel IN (0,1) OR @AuditLevel NOT IN (0,1,2,3) THEN 1 ELSE 0 END;

  SET @Applied = 0;

  BEGIN TRANSACTION;

  UPDATE [EverTask].[QueuedTasks]
  SET Status           = 'Completed',
      Exception        = NULL,
      LastExecutionUtc = @Now,
      ExecutionTimeMs  = @ExecutionTimeMs,
      NextRunUtc       = @NextRunUtc,
      CurrentRunCount  = CASE WHEN ISNULL(CurrentRunCount, 0) >= 2147483647 THEN 2147483647 ELSE ISNULL(CurrentRunCount, 0) + 1 END
  WHERE Id = @TaskId AND ScheduleVersion = @ExpectedScheduleVersion;

  IF @@ROWCOUNT = 0
  BEGIN
      ROLLBACK TRANSACTION;
      RETURN;
  END

  IF @ShouldStatusAudit = 1
      INSERT INTO [EverTask].[StatusAudit] (QueuedTaskId, UpdatedAtUtc, NewStatus, Exception)
      VALUES (@TaskId, @Now, 'Completed', NULL);

  IF @ShouldRunsAudit = 1
      INSERT INTO [EverTask].[RunsAudit] (QueuedTaskId, ExecutedAt, ExecutionTimeMs, Status, Exception)
      VALUES (@TaskId, @Now, @ExecutionTimeMs, 'Completed', NULL);

  SET @Applied = 1;
  COMMIT TRANSACTION;
END

INSERT INTO [EverTask].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260822182413_AddDurableOccurrences', N'<EFCORE-VERSION>');

COMMIT;
GO
