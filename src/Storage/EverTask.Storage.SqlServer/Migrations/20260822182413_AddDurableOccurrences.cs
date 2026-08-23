using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EverTask.Storage.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableOccurrences : Migration
    {
        private readonly ITaskStoreDbContext _dbContext;

        public AddDurableOccurrences(ITaskStoreDbContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentTaskId",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RuntimeInfo",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScheduleVersion",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_QueuedTasks_ParentTaskId",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                column: "ParentTaskId");

            // Filtered: SQL Server treats NULLs as equal in a unique index, and every ordinary row has a
            // null ParentTaskId — without the filter the second such row would violate it.
            migrationBuilder.CreateIndex(
                name: "UX_QueuedTasks_Occurrence",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                columns: new[] { "ParentTaskId", "ScheduledExecutionUtc" },
                unique: true,
                filter: "[ParentTaskId] IS NOT NULL AND [ScheduledExecutionUtc] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_QueuedTasks_OccurrenceSlot",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                sql: "ParentTaskId IS NULL OR ScheduledExecutionUtc IS NOT NULL");

            // Restrict, never cascade: SQL Server rejects a cascading self-reference, and the foreign key is
            // what stops a Remove(schedule) racing a materializer from leaving orphaned occurrences.
            migrationBuilder.AddForeignKey(
                name: "FK_QueuedTasks_QueuedTasks_ParentTaskId",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                column: "ParentTaskId",
                principalSchema: _dbContext.Schema,
                principalTable: "QueuedTasks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            CreateDurableOccurrenceProcedures(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var schema = string.IsNullOrEmpty(_dbContext.Schema) ? "dbo" : _dbContext.Schema;

            // The four procedures are NEW in this migration (the three pre-existing hot-write procs are
            // untouched by it), so undoing means dropping them — there is no previous body to restore.
            migrationBuilder.Sql($"DROP PROCEDURE IF EXISTS [{schema}].[usp_MaterializeOccurrence]");
            migrationBuilder.Sql($"DROP PROCEDURE IF EXISTS [{schema}].[usp_CancelSchedule]");
            migrationBuilder.Sql($"DROP PROCEDURE IF EXISTS [{schema}].[usp_UpdateCurrentRunCas]");
            migrationBuilder.Sql($"DROP PROCEDURE IF EXISTS [{schema}].[usp_CompleteRecurringRunCas]");

            migrationBuilder.DropForeignKey(
                name: "FK_QueuedTasks_QueuedTasks_ParentTaskId",
                schema: _dbContext.Schema,
                table: "QueuedTasks");

            migrationBuilder.DropIndex(
                name: "IX_QueuedTasks_ParentTaskId",
                schema: _dbContext.Schema,
                table: "QueuedTasks");

            migrationBuilder.DropIndex(
                name: "UX_QueuedTasks_Occurrence",
                schema: _dbContext.Schema,
                table: "QueuedTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_QueuedTasks_OccurrenceSlot",
                schema: _dbContext.Schema,
                table: "QueuedTasks");

            migrationBuilder.DropColumn(
                name: "ParentTaskId",
                schema: _dbContext.Schema,
                table: "QueuedTasks");

            migrationBuilder.DropColumn(
                name: "RuntimeInfo",
                schema: _dbContext.Schema,
                table: "QueuedTasks");

            migrationBuilder.DropColumn(
                name: "ScheduleVersion",
                schema: _dbContext.Schema,
                table: "QueuedTasks");
        }

        /// <summary>
        /// The new operations that run at every occurrence, as stored procedures — the same tier as the three
        /// pre-existing hot writes. Each is a single round-trip and a single transaction; the compare-and-swap
        /// lives in the procedure, so two hosts computing from the same cursor cannot both advance it.
        /// </summary>
        private void CreateDurableOccurrenceProcedures(MigrationBuilder migrationBuilder)
        {
            var schema = string.IsNullOrEmpty(_dbContext.Schema) ? "dbo" : _dbContext.Schema;

            // Inserts one occurrence AND advances the schedule cursor in one transaction. @Outcome mirrors
            // OccurrenceMaterializationOutcome: 0 Created, 1 AlreadyExists, 2 CursorMoved, 3 VersionMismatch,
            // 4 ParentInactive. UPDLOCK/HOLDLOCK on the schedule row is what serializes two materializers
            // reading the same cursor; without it both would pass the checks and one advance would be lost.
            migrationBuilder.Sql($@"DROP PROCEDURE IF EXISTS [{schema}].[usp_MaterializeOccurrence]");
            migrationBuilder.Sql($@"
CREATE PROCEDURE [{schema}].[usp_MaterializeOccurrence]
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
  FROM [{schema}].[QueuedTasks] WITH (UPDLOCK, HOLDLOCK)
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

  IF EXISTS (SELECT 1 FROM [{schema}].[QueuedTasks]
             WHERE ParentTaskId = @ParentId AND ScheduledExecutionUtc = @SlotUtc)
  BEGIN
      SET @Outcome = 1;
      ROLLBACK TRANSACTION;
      RETURN;
  END

  INSERT INTO [{schema}].[QueuedTasks]
      (Id, CreatedAtUtc, ExecutionTimeMs, ScheduledExecutionUtc, Type, Request, Handler, IsRecurring,
       CurrentRunCount, QueueName, AuditLevel, Status, ParentTaskId, RuntimeInfo, ScheduleVersion)
  VALUES
      (@OccurrenceId, @CreatedAtUtc, 0, @SlotUtc, @Type, @Request, @Handler, 0,
       0, @QueueName, @OccurrenceAuditLevel, 'WaitingQueue', @ParentId, @RuntimeInfo, @ExpectedScheduleVersion);

  -- A null new cursor ENDS the series, in this same commit: a separate finalization would leave a window
  -- in which a crash resurrects a series that has already produced its last occurrence.
  UPDATE [{schema}].[QueuedTasks]
  SET NextRunUtc       = @NewCursorUtc,
      CurrentRunCount  = CASE WHEN ISNULL(CurrentRunCount, 0) >= 2147483647 THEN 2147483647 ELSE ISNULL(CurrentRunCount, 0) + 1 END,
      Status           = CASE WHEN @NewCursorUtc IS NULL THEN 'Completed' ELSE Status END,
      Exception        = CASE WHEN @NewCursorUtc IS NULL THEN NULL ELSE Exception END,
      LastExecutionUtc = CASE WHEN @NewCursorUtc IS NULL THEN @Now ELSE LastExecutionUtc END
  WHERE Id = @ParentId;

  IF @NewCursorUtc IS NULL AND @AuditLevel = 0
      INSERT INTO [{schema}].[StatusAudit] (QueuedTaskId, UpdatedAtUtc, NewStatus, Exception)
      VALUES (@ParentId, @Now, 'Completed', NULL);

  COMMIT TRANSACTION;
END
");

            // Cancels a schedule AND its still-waiting occurrences in one transaction, so a materializer
            // racing the cancel can only ever see an inactive schedule. Occurrences already InProgress own a
            // live delivery and are left to finish on their own.
            migrationBuilder.Sql($@"DROP PROCEDURE IF EXISTS [{schema}].[usp_CancelSchedule]");
            migrationBuilder.Sql($@"
CREATE PROCEDURE [{schema}].[usp_CancelSchedule]
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

  UPDATE [{schema}].[QueuedTasks]
  SET Status = 'Cancelled'
  WHERE Id = @ParentId;

  SET @ParentCancelled = @@ROWCOUNT;

  UPDATE [{schema}].[QueuedTasks]
  SET Status = 'Cancelled'
  OUTPUT inserted.Id INTO @Cancelled
  WHERE ParentTaskId = @ParentId AND Status IN ('WaitingQueue', 'Queued', 'Pending');

  -- Cancelled carries no exception, so only AuditLevel.Full (0) audits it. The schedule row is audited only
  -- when the update actually found it: cancelling a schedule a concurrent Remove already deleted is a silent
  -- no-op on every other provider, while an audit row for a missing task violates the foreign key.
  IF @AuditLevel = 0
  BEGIN
      IF @ParentCancelled > 0
          INSERT INTO [{schema}].[StatusAudit] (QueuedTaskId, UpdatedAtUtc, NewStatus, Exception)
          VALUES (@ParentId, @Now, 'Cancelled', NULL);

      INSERT INTO [{schema}].[StatusAudit] (QueuedTaskId, UpdatedAtUtc, NewStatus, Exception)
      SELECT Id, @Now, 'Cancelled', NULL FROM @Cancelled;
  END

  COMMIT TRANSACTION;
END
");

            // usp_UpdateCurrentRun with the schedule version in the WHERE: a run that finishes after a
            // reschedule writes nothing and reports the loss, instead of forcing its stale next run.
            migrationBuilder.Sql($@"DROP PROCEDURE IF EXISTS [{schema}].[usp_UpdateCurrentRunCas]");
            migrationBuilder.Sql($@"
CREATE PROCEDURE [{schema}].[usp_UpdateCurrentRunCas]
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
  FROM [{schema}].[QueuedTasks] WITH (UPDLOCK, HOLDLOCK)
  WHERE Id = @TaskId AND ScheduleVersion = @ExpectedScheduleVersion;

  IF @@ROWCOUNT = 0
  BEGIN
      ROLLBACK TRANSACTION;
      RETURN;
  END

  IF @AuditLevel IN (0, 1)
      SET @ShouldAudit = 1;
  ELSE IF @AuditLevel = 2 AND (@Status = 'Failed' OR (@Exception IS NOT NULL AND @Exception <> ''))
      SET @ShouldAudit = 1;

  UPDATE [{schema}].[QueuedTasks]
  SET ExecutionTimeMs = @ExecutionTimeMs,
      NextRunUtc = @NextRunUtc,
      CurrentRunCount = CASE WHEN ISNULL(CurrentRunCount, 0) >= 2147483647 THEN 2147483647 ELSE ISNULL(CurrentRunCount, 0) + 1 END
  WHERE Id = @TaskId;

  IF @ShouldAudit = 1
  BEGIN
      INSERT INTO [{schema}].[RunsAudit] (QueuedTaskId, ExecutedAt, ExecutionTimeMs, Status, Exception)
      VALUES (@TaskId, @Now, @ExecutionTimeMs, @Status, @Exception);
  END

  SET @Applied = 1;
  COMMIT TRANSACTION;
END
");

            migrationBuilder.Sql($@"DROP PROCEDURE IF EXISTS [{schema}].[usp_CompleteRecurringRunCas]");
            migrationBuilder.Sql($@"
CREATE PROCEDURE [{schema}].[usp_CompleteRecurringRunCas]
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
  DECLARE @ShouldStatusAudit BIT = CASE WHEN @AuditLevel = 0      THEN 1 ELSE 0 END;
  DECLARE @ShouldRunsAudit   BIT = CASE WHEN @AuditLevel IN (0,1) THEN 1 ELSE 0 END;

  SET @Applied = 0;

  BEGIN TRANSACTION;

  UPDATE [{schema}].[QueuedTasks]
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
      INSERT INTO [{schema}].[StatusAudit] (QueuedTaskId, UpdatedAtUtc, NewStatus, Exception)
      VALUES (@TaskId, @Now, 'Completed', NULL);

  IF @ShouldRunsAudit = 1
      INSERT INTO [{schema}].[RunsAudit] (QueuedTaskId, ExecutedAt, ExecutionTimeMs, Status, Exception)
      VALUES (@TaskId, @Now, @ExecutionTimeMs, 'Completed', NULL);

  SET @Applied = 1;
  COMMIT TRANSACTION;
END
");
        }
    }
}
