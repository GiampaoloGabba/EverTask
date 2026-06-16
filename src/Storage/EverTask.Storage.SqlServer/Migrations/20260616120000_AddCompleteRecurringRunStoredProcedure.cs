using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EverTask.Storage.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddCompleteRecurringRunStoredProcedure : Migration
    {
        private readonly ITaskStoreDbContext _dbContext;

        public AddCompleteRecurringRunStoredProcedure(ITaskStoreDbContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var schema = string.IsNullOrEmpty(_dbContext.Schema) ? "dbo" : _dbContext.Schema;

            // Single-roundtrip counterpart of EfCoreTaskStorage.CompleteRecurringRun: marks a recurring
            // occurrence Completed AND advances the run counter / next run in ONE atomic transaction, so a
            // crash can never split the two and resurrect the finished occurrence at recovery (CU14/L29).
            // Unlike usp_UpdateCurrentRun there is NO read-then-decide (the target status is the constant
            // 'Completed'), so the proc carries NO SELECT and NO lock hint: the row-level UPDATE is already
            // atomic and the at-least-once delivery registry excludes a second concurrent completion of the
            // same occurrence (it does not coordinate against a racing Cancel, exactly as the EF base path).
            //
            // Audit thresholds for the (Completed, no-exception) transition differ per table:
            //   StatusAudit -> only AuditLevel 0 (Full)
            //   RunsAudit   -> AuditLevel 0 (Full) and 1 (Minimal)
            // The run counter ALWAYS advances by exactly one real execution (Option B accounting),
            // never gated on the audit level.
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
  -- Two distinct audit flags: the (Completed, null) transition has different thresholds per table
  DECLARE @ShouldStatusAudit BIT = CASE WHEN @AuditLevel = 0      THEN 1 ELSE 0 END;  -- StatusAudit: Full only
  DECLARE @ShouldRunsAudit   BIT = CASE WHEN @AuditLevel IN (0,1) THEN 1 ELSE 0 END;  -- RunsAudit: Full + Minimal

  BEGIN TRANSACTION;

  -- Full column set (Status/Exception/LastExecutionUtc that usp_UpdateCurrentRun does NOT touch).
  -- NextRunUtc is assigned UNCONDITIONALLY (never COALESCE): a NULL makes the series terminal and
  -- non-recoverable; preserving the old value would resurrect a finished series (double execution).
  UPDATE [{schema}].[QueuedTasks]
  SET Status           = 'Completed',
      Exception        = NULL,
      LastExecutionUtc = @Now,
      ExecutionTimeMs  = @ExecutionTimeMs,
      NextRunUtc       = @NextRunUtc,
      CurrentRunCount  = ISNULL(CurrentRunCount, 0) + 1
  WHERE Id = @TaskId;

  IF @@ROWCOUNT = 0  -- task missing: no orphan audit, no throw (mirrors the EF base early return)
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

            migrationBuilder.Sql($@"
DROP PROCEDURE IF EXISTS [{schema}].[usp_CompleteRecurringRun]
");
        }
    }
}
