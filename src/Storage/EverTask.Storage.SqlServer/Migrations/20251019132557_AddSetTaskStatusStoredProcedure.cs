using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EverTask.Storage.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddSetTaskStatusStoredProcedure : Migration
    {
        private readonly ITaskStoreDbContext _dbContext;

        public AddSetTaskStatusStoredProcedure(ITaskStoreDbContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var schema = string.IsNullOrEmpty(_dbContext.Schema) ? "dbo" : _dbContext.Schema;

            migrationBuilder.Sql($@"
CREATE PROCEDURE [{schema}].[usp_SetTaskStatus]
    @TaskId UNIQUEIDENTIFIER,
    @Status NVARCHAR(15),
    @Exception NVARCHAR(MAX) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Now DATETIMEOFFSET = SYSDATETIMEOFFSET();
    DECLARE @LastExecutionUtc DATETIMEOFFSET = NULL;

    -- Replica ESATTA della logica C# (EfCoreTaskStorage.cs:87-92)
    -- LastExecutionUtc = NOW() per status terminali (Completed, Failed, ServiceStopped, WaitingQueue)
    -- LastExecutionUtc = NULL per status intermedi (Queued, InProgress, Cancelled, Pending)
    IF @Status NOT IN ('Queued', 'InProgress', 'Cancelled', 'Pending')
        SET @LastExecutionUtc = @Now;

    BEGIN TRANSACTION;

    -- Update task first (replica ExecuteUpdateAsync EfCoreTaskStorage.cs:99-105)
    UPDATE [{schema}].[QueuedTasks]
    SET Status = @Status,
        LastExecutionUtc = @LastExecutionUtc,
        Exception = @Exception
    WHERE Id = @TaskId;

    -- Only proceed with audit if task was found
    IF @@ROWCOUNT > 0
    BEGIN
        -- Insert audit (replica Audit method EfCoreTaskStorage.cs:118-139)
        INSERT INTO [{schema}].[StatusAudit] (QueuedTaskId, UpdatedAtUtc, NewStatus, Exception)
        VALUES (@TaskId, @Now, @Status, @Exception);

        COMMIT TRANSACTION;
    END
    ELSE
    BEGIN
        -- Task not found - rollback (replica line 108-110 warning)
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
DROP PROCEDURE [{schema}].[usp_SetTaskStatus]
");
        }
    }
}
