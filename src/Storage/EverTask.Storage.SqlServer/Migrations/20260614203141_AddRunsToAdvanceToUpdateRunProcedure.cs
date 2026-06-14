using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EverTask.Storage.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddRunsToAdvanceToUpdateRunProcedure : Migration
    {
        private readonly ITaskStoreDbContext _dbContext;

        public AddRunsToAdvanceToUpdateRunProcedure(ITaskStoreDbContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var schema = string.IsNullOrEmpty(_dbContext.Schema) ? "dbo" : _dbContext.Schema;

            // Add an optional @RunsToAdvance parameter (default 1) so a single roundtrip can also
            // account for occurrences skipped during a downtime: the recurring stop-check counts
            // skipped occurrences toward MaxRuns, so the persisted CurrentRunCount must advance by
            // 1 + skipped, not by a fixed 1 (F7/F8). Defaulting to 1 keeps the proc backward compatible
            // with any caller that does not pass the new parameter.
            migrationBuilder.Sql($@"
DROP PROCEDURE IF EXISTS [{schema}].[usp_UpdateCurrentRun]
");

            migrationBuilder.Sql($@"
CREATE PROCEDURE [{schema}].[usp_UpdateCurrentRun]
  @TaskId UNIQUEIDENTIFIER,
  @ExecutionTimeMs FLOAT,
  @NextRunUtc DATETIMEOFFSET = NULL,
  @AuditLevel INT = 0,
  @RunsToAdvance INT = 1
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @Now DATETIMEOFFSET = SWITCHOFFSET(SYSDATETIMEOFFSET(), '+00:00');
  DECLARE @Status NVARCHAR(15);
  DECLARE @Exception NVARCHAR(MAX);
  DECLARE @ShouldAudit BIT = 0;

  -- Skipped occurrences must count toward the run counter; never advance by less than 1.
  IF @RunsToAdvance IS NULL OR @RunsToAdvance < 1
      SET @RunsToAdvance = 1;

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
      CurrentRunCount = ISNULL(CurrentRunCount, 0) + @RunsToAdvance
  WHERE Id = @TaskId;

  IF @ShouldAudit = 1
  BEGIN
      INSERT INTO [{schema}].[RunsAudit] (QueuedTaskId, ExecutedAt, ExecutionTimeMs, Status, Exception)
      VALUES (@TaskId, @Now, @ExecutionTimeMs, @Status, @Exception);
  END

  COMMIT TRANSACTION;
END
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var schema = string.IsNullOrEmpty(_dbContext.Schema) ? "dbo" : _dbContext.Schema;

            migrationBuilder.Sql($@"
DROP PROCEDURE IF EXISTS [{schema}].[usp_UpdateCurrentRun]
");

            // Restore the previous version of the procedure (AddRecoveryIndexAndUpdateRunProcedure),
            // which always advanced the counter by exactly 1.
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
        }
    }
}
