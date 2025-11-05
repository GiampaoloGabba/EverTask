using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EverTask.Storage.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class FilterServiceStoppedOperationCancelled : Migration
    {
        private readonly ITaskStoreDbContext _dbContext;

        public FilterServiceStoppedOperationCancelled(ITaskStoreDbContext dbContext)
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

        // Recreate stored procedure with updated audit logic
        migrationBuilder.Sql($@"
CREATE PROCEDURE [{schema}].[usp_SetTaskStatus]
  @TaskId UNIQUEIDENTIFIER,
  @Status NVARCHAR(15),
  @Exception NVARCHAR(MAX) = NULL,
  @AuditLevel INT = 0
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

  UPDATE [{schema}].[QueuedTasks]
  SET Status = @Status,
      LastExecutionUtc = @LastExecutionUtc,
      Exception = @Exception
  WHERE Id = @TaskId;

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

            // Drop existing stored procedure
            migrationBuilder.Sql($@"
DROP PROCEDURE IF EXISTS [{schema}].[usp_SetTaskStatus]
");

            // Recreate stored procedure with AuditLevel parameter
            migrationBuilder.Sql($@"
CREATE PROCEDURE [{schema}].[usp_SetTaskStatus]
    @TaskId UNIQUEIDENTIFIER,
    @Status NVARCHAR(15),
    @Exception NVARCHAR(MAX) = NULL,
    @AuditLevel INT = 0  -- Default to Full (0) for backward compatibility
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Now DATETIMEOFFSET = SYSDATETIMEOFFSET();
    DECLARE @LastExecutionUtc DATETIMEOFFSET = NULL;
    DECLARE @ShouldAudit BIT = 0;

    -- Replica ESATTA della logica C# (EfCoreTaskStorage.cs:114-119)
    -- LastExecutionUtc = NOW() per status terminali (Completed, Failed, ServiceStopped)
    -- LastExecutionUtc = NULL per status intermedi (Queued, InProgress, Cancelled, Pending)
    IF @Status NOT IN ('Queued', 'InProgress', 'Cancelled', 'Pending')
        SET @LastExecutionUtc = @Now;

    -- Replica logica ShouldCreateStatusAudit (EfCoreTaskStorage.cs:155-163)
    -- AuditLevel.None = 3 -> false
    -- AuditLevel.ErrorsOnly = 2 -> exception != null || status is Failed or ServiceStopped
    -- AuditLevel.Minimal = 1 -> exception != null || status is Failed or ServiceStopped
    -- AuditLevel.Full = 0 -> true
    IF @AuditLevel = 0  -- Full
        SET @ShouldAudit = 1;
    ELSE IF @AuditLevel IN (1, 2)  -- Minimal or ErrorsOnly
    BEGIN
        IF @Exception IS NOT NULL OR @Status IN ('Failed', 'ServiceStopped')
            SET @ShouldAudit = 1;
    END
    -- ELSE @AuditLevel = 3 (None) -> @ShouldAudit remains 0

    BEGIN TRANSACTION;

    -- Update task first (replica ExecuteUpdateAsync EfCoreTaskStorage.cs:123-129)
    UPDATE [{schema}].[QueuedTasks]
    SET Status = @Status,
        LastExecutionUtc = @LastExecutionUtc,
        Exception = @Exception
    WHERE Id = @TaskId;

    -- Only proceed if task was found
    IF @@ROWCOUNT > 0
    BEGIN
        -- Insert audit only if AuditLevel requires it
        IF @ShouldAudit = 1
        BEGIN
            INSERT INTO [{schema}].[StatusAudit] (QueuedTaskId, UpdatedAtUtc, NewStatus, Exception)
            VALUES (@TaskId, @Now, @Status, @Exception);
        END

        COMMIT TRANSACTION;
    END
    ELSE
    BEGIN
        -- Task not found - rollback (replica line 132-134 warning)
        ROLLBACK TRANSACTION;
    END
END
");
        }
    }
}
