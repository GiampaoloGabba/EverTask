using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EverTask.Storage.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class RenameStatusAudit : Migration
    {
        private readonly ITaskStoreDbContext _dbContext;

        public RenameStatusAudit(ITaskStoreDbContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            TargetModel.GetDefaultSchema();

            migrationBuilder.DropForeignKey(
                name: "FK_QueuedTaskStatusAudit_QueuedTasks_QueuedTaskId",
                schema: _dbContext.Schema,
                table: "QueuedTaskStatusAudit");

            migrationBuilder.DropPrimaryKey(
                name: "PK_QueuedTaskStatusAudit",
                schema: _dbContext.Schema,
                table: "QueuedTaskStatusAudit");

            migrationBuilder.RenameTable(
                name: "QueuedTaskStatusAudit",
                schema: _dbContext.Schema,
                newName: "StatusAudit",
                newSchema: "EverTask");

            migrationBuilder.RenameIndex(
                name: "IX_QueuedTaskStatusAudit_QueuedTaskId",
                schema: _dbContext.Schema,
                table: "StatusAudit",
                newName: "IX_StatusAudit_QueuedTaskId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_StatusAudit",
                schema: _dbContext.Schema,
                table: "StatusAudit",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_StatusAudit_QueuedTasks_QueuedTaskId",
                schema: _dbContext.Schema,
                table: "StatusAudit",
                column: "QueuedTaskId",
                principalSchema: "EverTask",
                principalTable: "QueuedTasks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StatusAudit_QueuedTasks_QueuedTaskId",
                schema: _dbContext.Schema,
                table: "StatusAudit");

            migrationBuilder.DropPrimaryKey(
                name: "PK_StatusAudit",
                schema: _dbContext.Schema,
                table: "StatusAudit");

            migrationBuilder.RenameTable(
                name: "StatusAudit",
                schema: _dbContext.Schema,
                newName: "QueuedTaskStatusAudit",
                newSchema: "EverTask");

            migrationBuilder.RenameIndex(
                name: "IX_StatusAudit_QueuedTaskId",
                schema: _dbContext.Schema,
                table: "QueuedTaskStatusAudit",
                newName: "IX_QueuedTaskStatusAudit_QueuedTaskId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_QueuedTaskStatusAudit",
                schema: _dbContext.Schema,
                table: "QueuedTaskStatusAudit",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_QueuedTaskStatusAudit_QueuedTasks_QueuedTaskId",
                schema: _dbContext.Schema,
                table: "QueuedTaskStatusAudit",
                column: "QueuedTaskId",
                principalSchema: "EverTask",
                principalTable: "QueuedTasks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
