using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EverTask.Storage.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class SaturateRunCounter : Migration
    {
        private readonly ITaskStoreDbContext _dbContext;

        public SaturateRunCounter(ITaskStoreDbContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var schema = string.IsNullOrEmpty(_dbContext.Schema) ? "dbo" : _dbContext.Schema;

            // Saturate the run counter at int.MaxValue instead of overflowing. An unbounded recurring series
            // (MaxRuns NULL) that reaches int.MaxValue real executions kept advancing CurrentRunCount = +1,
            // which raised an arithmetic-overflow error on the integer column (the C# paths wrapped silently to
            // int.MinValue). Now every increment site caps at int.MaxValue, so the series keeps running with the
            // counter frozen at its max. Behavior matches the EF base C#, the Postgres CTEs and MemoryTaskStorage.

            migrationBuilder.Sql($@"DROP PROCEDURE IF EXISTS [{schema}].[usp_UpdateCurrentRun]");
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

  SELECT @Status = Status, @Exception = Exception
  FROM [{schema}].[QueuedTasks] WITH (UPDLOCK, HOLDLOCK)
  WHERE Id = @TaskId;

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

  COMMIT TRANSACTION;
END
");

            migrationBuilder.Sql($@"DROP PROCEDURE IF EXISTS [{schema}].[usp_CompleteRecurringRun]");
            migrationBuilder.Sql($@"
CREATE PROCEDURE [{schema}].[usp_CompleteRecurringRun]
  @TaskId UNIQUEIDENTIFIER,
  @ExecutionTimeMs FLOAT,
  @NextRunUtc DATETIMEOFFSET = NULL,
  @AuditLevel INT = 0
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @Now DATETIMEOFFSET = SWITCHOFFSET(SYSDATETIMEOFFSET(), '+00:00');
  DECLARE @ShouldStatusAudit BIT = CASE WHEN @AuditLevel = 0      THEN 1 ELSE 0 END;
  DECLARE @ShouldRunsAudit   BIT = CASE WHEN @AuditLevel IN (0,1) THEN 1 ELSE 0 END;

  BEGIN TRANSACTION;

  UPDATE [{schema}].[QueuedTasks]
  SET Status           = 'Completed',
      Exception        = NULL,
      LastExecutionUtc = @Now,
      ExecutionTimeMs  = @ExecutionTimeMs,
      NextRunUtc       = @NextRunUtc,
      CurrentRunCount  = CASE WHEN ISNULL(CurrentRunCount, 0) >= 2147483647 THEN 2147483647 ELSE ISNULL(CurrentRunCount, 0) + 1 END
  WHERE Id = @TaskId;

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

  COMMIT TRANSACTION;
END
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var schema = string.IsNullOrEmpty(_dbContext.Schema) ? "dbo" : _dbContext.Schema;

            // Restore the previous (overflowing) increment: CurrentRunCount = ISNULL(CurrentRunCount,0) + 1.

            migrationBuilder.Sql($@"DROP PROCEDURE IF EXISTS [{schema}].[usp_UpdateCurrentRun]");
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

  SELECT @Status = Status, @Exception = Exception
  FROM [{schema}].[QueuedTasks] WITH (UPDLOCK, HOLDLOCK)
  WHERE Id = @TaskId;

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

            migrationBuilder.Sql($@"DROP PROCEDURE IF EXISTS [{schema}].[usp_CompleteRecurringRun]");
            migrationBuilder.Sql($@"
CREATE PROCEDURE [{schema}].[usp_CompleteRecurringRun]
  @TaskId UNIQUEIDENTIFIER,
  @ExecutionTimeMs FLOAT,
  @NextRunUtc DATETIMEOFFSET = NULL,
  @AuditLevel INT = 0
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @Now DATETIMEOFFSET = SWITCHOFFSET(SYSDATETIMEOFFSET(), '+00:00');
  DECLARE @ShouldStatusAudit BIT = CASE WHEN @AuditLevel = 0      THEN 1 ELSE 0 END;
  DECLARE @ShouldRunsAudit   BIT = CASE WHEN @AuditLevel IN (0,1) THEN 1 ELSE 0 END;

  BEGIN TRANSACTION;

  UPDATE [{schema}].[QueuedTasks]
  SET Status           = 'Completed',
      Exception        = NULL,
      LastExecutionUtc = @Now,
      ExecutionTimeMs  = @ExecutionTimeMs,
      NextRunUtc       = @NextRunUtc,
      CurrentRunCount  = ISNULL(CurrentRunCount, 0) + 1
  WHERE Id = @TaskId;

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

  COMMIT TRANSACTION;
END
");
        }
    }
}
