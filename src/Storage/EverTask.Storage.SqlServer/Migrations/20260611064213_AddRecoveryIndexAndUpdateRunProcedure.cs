using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EverTask.Storage.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddRecoveryIndexAndUpdateRunProcedure : Migration
    {
        private readonly ITaskStoreDbContext _dbContext;

        public AddRecoveryIndexAndUpdateRunProcedure(ITaskStoreDbContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var schema = string.IsNullOrEmpty(_dbContext.Schema) ? "dbo" : _dbContext.Schema;

            // Covering index for the startup-recovery query (RetrievePending).
            // The query filters on recoverable statuses OR (IsRecurring AND NextRunUtc) plus
            // MaxRuns/RunUntil, ordered by (CreatedAtUtc, Id) with keyset pagination: without
            // this index the OR over Status/IsRecurring degenerates into a clustered scan + sort
            // on large tables. The narrow index supports the ordered keyset scan with all filter
            // columns resolved as included columns; key lookups happen only for the rows returned.
            migrationBuilder.Sql($@"
CREATE NONCLUSTERED INDEX [IX_QueuedTasks_Recovery]
ON [{schema}].[QueuedTasks] ([CreatedAtUtc], [Id])
INCLUDE ([Status], [IsRecurring], [NextRunUtc], [MaxRuns], [CurrentRunCount], [RunUntil])
");

            // Single-roundtrip counterpart of EfCoreTaskStorage.UpdateCurrentRun: reads
            // Status/Exception (audit decision), updates the run counters and inserts the
            // RunsAudit record in one atomic transaction.
            // AuditLevel mapping (EverTask.Abstractions.AuditLevel):
            //   0 = Full, 1 = Minimal -> always audit runs
            //   2 = ErrorsOnly        -> audit only failed runs (Status 'Failed' or Exception set)
            //   3 = None              -> never audit
            migrationBuilder.Sql($@"
CREATE PROCEDURE [{schema}].[usp_UpdateCurrentRun]
  @TaskId UNIQUEIDENTIFIER,
  @ExecutionTimeMs FLOAT,
  @NextRunUtc DATETIMEOFFSET = NULL,
  @AuditLevel INT = 0
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @Now DATETIMEOFFSET = SWITCHOFFSET(SYSDATETIMEOFFSET(), '+00:00');
  DECLARE @Status NVARCHAR(15);
  DECLARE @Exception NVARCHAR(MAX);
  DECLARE @ShouldAudit BIT = 0;

  BEGIN TRANSACTION;

  -- Lock the row so the audit decision and the update are consistent
  SELECT @Status = Status, @Exception = Exception
  FROM [{schema}].[QueuedTasks] WITH (UPDLOCK, HOLDLOCK)
  WHERE Id = @TaskId;

  IF @@ROWCOUNT = 0
  BEGIN
      ROLLBACK TRANSACTION;
      RETURN;
  END

  IF @AuditLevel IN (0, 1)  -- Full, Minimal: always create RunsAudit
      SET @ShouldAudit = 1;
  ELSE IF @AuditLevel = 2 AND (@Status = 'Failed' OR (@Exception IS NOT NULL AND @Exception <> ''))
      SET @ShouldAudit = 1;  -- ErrorsOnly: failed runs only
  -- AuditLevel 3 (None): no audit

  UPDATE [{schema}].[QueuedTasks]
  SET ExecutionTimeMs = @ExecutionTimeMs,
      NextRunUtc = @NextRunUtc,
      CurrentRunCount = ISNULL(CurrentRunCount, 0) + 1
  WHERE Id = @TaskId;

  IF @ShouldAudit = 1
  BEGIN
      INSERT INTO [{schema}].[RunsAudit] (QueuedTaskId, ExecutedAt, ExecutionTimeMs, Status, Exception)
      VALUES (@TaskId, @Now, @ExecutionTimeMs, @Status, @Exception);
  END

  COMMIT TRANSACTION;
END
");

            // usp_SetTaskStatus: two fixes aligned with EfCoreTaskStorage.SetStatus.
            // 1. WaitingQueue joins the intermediate statuses: a full-queue revert must not stamp
            //    a fake execution time on a task that never ran.
            // 2. Intermediate statuses PRESERVE LastExecutionUtc (COALESCE) instead of nulling it:
            //    re-queueing a recurring task no longer wipes the timestamp of its last real run.
            migrationBuilder.Sql($@"
DROP PROCEDURE IF EXISTS [{schema}].[usp_SetTaskStatus]
");

            migrationBuilder.Sql($@"
CREATE PROCEDURE [{schema}].[usp_SetTaskStatus]
  @TaskId UNIQUEIDENTIFIER,
  @Status NVARCHAR(15),
  @Exception NVARCHAR(MAX) = NULL,
  @AuditLevel INT = 0,
  @ExecutionTimeMs FLOAT = NULL
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @Now DATETIMEOFFSET = SWITCHOFFSET(SYSDATETIMEOFFSET(), '+00:00');
  DECLARE @LastExecutionUtc DATETIMEOFFSET = NULL;
  DECLARE @ShouldAudit BIT = 0;

  -- LastExecutionUtc only on terminal transitions; intermediate statuses
  -- (WaitingQueue, Queued, InProgress, Cancelled, Pending) preserve the previous value
  IF @Status NOT IN ('WaitingQueue', 'Queued', 'InProgress', 'Cancelled', 'Pending')
      SET @LastExecutionUtc = @Now;

  -- Audit logic: filter OperationCanceledException on ServiceStopped
  IF @AuditLevel = 0  -- Full
      SET @ShouldAudit = 1;
  ELSE IF @AuditLevel IN (1, 2)  -- Minimal or ErrorsOnly
  BEGIN
      -- IsRealError logic
      DECLARE @IsRealError BIT = 0;

      -- Always audit Failed status
      IF @Status = 'Failed'
          SET @IsRealError = 1;
      -- ServiceStopped with OperationCanceledException is expected shutdown, not an error
      ELSE IF @Status = 'ServiceStopped' AND @Exception LIKE '%OperationCanceledException%'
          SET @IsRealError = 0;
      -- All other exceptions are real errors
      ELSE IF @Exception IS NOT NULL
          SET @IsRealError = 1;

      SET @ShouldAudit = @IsRealError;
  END

  BEGIN TRANSACTION;

  -- Update task - conditionally include ExecutionTimeMs if provided
  IF @ExecutionTimeMs IS NOT NULL
  BEGIN
      UPDATE [{schema}].[QueuedTasks]
      SET Status = @Status,
          LastExecutionUtc = COALESCE(@LastExecutionUtc, LastExecutionUtc),
          Exception = @Exception,
          ExecutionTimeMs = @ExecutionTimeMs
      WHERE Id = @TaskId;
  END
  ELSE
  BEGIN
      UPDATE [{schema}].[QueuedTasks]
      SET Status = @Status,
          LastExecutionUtc = COALESCE(@LastExecutionUtc, LastExecutionUtc),
          Exception = @Exception
      WHERE Id = @TaskId;
  END

  IF @@ROWCOUNT > 0
  BEGIN
      IF @ShouldAudit = 1
      BEGIN
          INSERT INTO [{schema}].[StatusAudit] (QueuedTaskId, UpdatedAtUtc, NewStatus, Exception)
          VALUES (@TaskId, @Now, @Status, @Exception);
      END

      COMMIT TRANSACTION;
  END
  ELSE
  BEGIN
      ROLLBACK TRANSACTION;
  END
END
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var schema = string.IsNullOrEmpty(_dbContext.Schema) ? "dbo" : _dbContext.Schema;

            migrationBuilder.Sql($@"
DROP INDEX [IX_QueuedTasks_Recovery] ON [{schema}].[QueuedTasks]
");

            migrationBuilder.Sql($@"
DROP PROCEDURE IF EXISTS [{schema}].[usp_UpdateCurrentRun]
");

            migrationBuilder.Sql($@"
DROP PROCEDURE IF EXISTS [{schema}].[usp_SetTaskStatus]
");

            // Restore previous usp_SetTaskStatus (FixStoredProcedureUtcTimezone version)
            migrationBuilder.Sql($@"
CREATE PROCEDURE [{schema}].[usp_SetTaskStatus]
  @TaskId UNIQUEIDENTIFIER,
  @Status NVARCHAR(15),
  @Exception NVARCHAR(MAX) = NULL,
  @AuditLevel INT = 0,
  @ExecutionTimeMs FLOAT = NULL
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @Now DATETIMEOFFSET = SWITCHOFFSET(SYSDATETIMEOFFSET(), '+00:00');
  DECLARE @LastExecutionUtc DATETIMEOFFSET = NULL;
  DECLARE @ShouldAudit BIT = 0;

  IF @Status NOT IN ('Queued', 'InProgress', 'Cancelled', 'Pending')
      SET @LastExecutionUtc = @Now;

  -- Updated audit logic: filter OperationCanceledException on ServiceStopped
  IF @AuditLevel = 0  -- Full
      SET @ShouldAudit = 1;
  ELSE IF @AuditLevel IN (1, 2)  -- Minimal or ErrorsOnly
  BEGIN
      -- IsRealError logic
      DECLARE @IsRealError BIT = 0;

      -- Always audit Failed status
      IF @Status = 'Failed'
          SET @IsRealError = 1;
      -- ServiceStopped with OperationCanceledException is expected shutdown, not an error
      ELSE IF @Status = 'ServiceStopped' AND @Exception LIKE '%OperationCanceledException%'
          SET @IsRealError = 0;
      -- All other exceptions are real errors
      ELSE IF @Exception IS NOT NULL
          SET @IsRealError = 1;

      SET @ShouldAudit = @IsRealError;
  END

  BEGIN TRANSACTION;

  -- Update task - conditionally include ExecutionTimeMs if provided
  IF @ExecutionTimeMs IS NOT NULL
  BEGIN
      UPDATE [{schema}].[QueuedTasks]
      SET Status = @Status,
          LastExecutionUtc = @LastExecutionUtc,
          Exception = @Exception,
          ExecutionTimeMs = @ExecutionTimeMs
      WHERE Id = @TaskId;
  END
  ELSE
  BEGIN
      UPDATE [{schema}].[QueuedTasks]
      SET Status = @Status,
          LastExecutionUtc = @LastExecutionUtc,
          Exception = @Exception
      WHERE Id = @TaskId;
  END

  IF @@ROWCOUNT > 0
  BEGIN
      IF @ShouldAudit = 1
      BEGIN
          INSERT INTO [{schema}].[StatusAudit] (QueuedTaskId, UpdatedAtUtc, NewStatus, Exception)
          VALUES (@TaskId, @Now, @Status, @Exception);
      END

      COMMIT TRANSACTION;
  END
  ELSE
  BEGIN
      ROLLBACK TRANSACTION;
  END
END
");
        }
    }
}
