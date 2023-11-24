using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EverTask.Storage.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class RenameStatusAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_QueuedTaskStatusAudit_QueuedTasks_QueuedTaskId",
                schema: "EverTask",
                table: "QueuedTaskStatusAudit");

            migrationBuilder.DropPrimaryKey(
                name: "PK_QueuedTaskStatusAudit",
                schema: "EverTask",
                table: "QueuedTaskStatusAudit");

            migrationBuilder.RenameTable(
                name: "QueuedTaskStatusAudit",
                schema: "EverTask",
                newName: "StatusAudit",
                newSchema: "EverTask");

            migrationBuilder.RenameIndex(
                name: "IX_QueuedTaskStatusAudit_QueuedTaskId",
                schema: "EverTask",
                table: "StatusAudit",
                newName: "IX_StatusAudit_QueuedTaskId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_StatusAudit",
                schema: "EverTask",
                table: "StatusAudit",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_StatusAudit_QueuedTasks_QueuedTaskId",
                schema: "EverTask",
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
                schema: "EverTask",
                table: "StatusAudit");

            migrationBuilder.DropPrimaryKey(
                name: "PK_StatusAudit",
                schema: "EverTask",
                table: "StatusAudit");

            migrationBuilder.RenameTable(
                name: "StatusAudit",
                schema: "EverTask",
                newName: "QueuedTaskStatusAudit",
                newSchema: "EverTask");

            migrationBuilder.RenameIndex(
                name: "IX_StatusAudit_QueuedTaskId",
                schema: "EverTask",
                table: "QueuedTaskStatusAudit",
                newName: "IX_QueuedTaskStatusAudit_QueuedTaskId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_QueuedTaskStatusAudit",
                schema: "EverTask",
                table: "QueuedTaskStatusAudit",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_QueuedTaskStatusAudit_QueuedTasks_QueuedTaskId",
                schema: "EverTask",
                table: "QueuedTaskStatusAudit",
                column: "QueuedTaskId",
                principalSchema: "EverTask",
                principalTable: "QueuedTasks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
