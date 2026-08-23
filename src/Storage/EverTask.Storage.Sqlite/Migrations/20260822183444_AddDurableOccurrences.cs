using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EverTask.Storage.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableOccurrences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentTaskId",
                table: "QueuedTasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RuntimeInfo",
                table: "QueuedTasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScheduleVersion",
                table: "QueuedTasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_QueuedTasks_ParentTaskId",
                table: "QueuedTasks",
                column: "ParentTaskId");

            migrationBuilder.CreateIndex(
                name: "UX_QueuedTasks_Occurrence",
                table: "QueuedTasks",
                columns: new[] { "ParentTaskId", "ScheduledExecutionUtc" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_QueuedTasks_OccurrenceSlot",
                table: "QueuedTasks",
                sql: "ParentTaskId IS NULL OR ScheduledExecutionUtc IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_QueuedTasks_QueuedTasks_ParentTaskId",
                table: "QueuedTasks",
                column: "ParentTaskId",
                principalTable: "QueuedTasks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_QueuedTasks_QueuedTasks_ParentTaskId",
                table: "QueuedTasks");

            migrationBuilder.DropIndex(
                name: "IX_QueuedTasks_ParentTaskId",
                table: "QueuedTasks");

            migrationBuilder.DropIndex(
                name: "UX_QueuedTasks_Occurrence",
                table: "QueuedTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_QueuedTasks_OccurrenceSlot",
                table: "QueuedTasks");

            migrationBuilder.DropColumn(
                name: "ParentTaskId",
                table: "QueuedTasks");

            migrationBuilder.DropColumn(
                name: "RuntimeInfo",
                table: "QueuedTasks");

            migrationBuilder.DropColumn(
                name: "ScheduleVersion",
                table: "QueuedTasks");
        }
    }
}
