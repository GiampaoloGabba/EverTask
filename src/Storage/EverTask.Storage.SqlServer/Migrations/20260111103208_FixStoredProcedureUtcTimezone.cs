using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EverTask.Storage.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class FixStoredProcedureUtcTimezone : Migration
    {
        private readonly ITaskStoreDbContext _dbContext;

        public FixStoredProcedureUtcTimezone(ITaskStoreDbContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var schema = string.IsNullOrEmpty(_dbContext.Schema) ? "dbo" : _dbContext.Schema;

            // Drop existing stored procedure
            migrationBuilder.Sql($@"
DROP PROCEDURE IF EXISTS [{schema}].[usp_SetTaskStatus]
");

            // Recreate stored procedure with UTC timezone fix (SWITCHOFFSET to +00:00)
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var schema = string.IsNullOrEmpty(_dbContext.Schema) ? "dbo" : _dbContext.Schema;

            // Drop updated stored procedure
            migrationBuilder.Sql($@"
DROP PROCEDURE IF EXISTS [{schema}].[usp_SetTaskStatus]
");

            // Restore previous stored procedure (with SYSDATETIMEOFFSET - original behavior)
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

  DECLARE @Now DATETIMEOFFSET = SYSDATETIMEOFFSET();
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
