using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EverTask.Storage.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableOccurrences : Migration
    {
        private readonly ITaskStoreDbContext _dbContext;

        public AddDurableOccurrences(ITaskStoreDbContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentTaskId",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RuntimeInfo",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScheduleVersion",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_QueuedTasks_ParentTaskId",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                column: "ParentTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_QueuedTasks_ParentTaskId_Status",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                columns: new[] { "ParentTaskId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_QueuedTasks_Occurrence",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                columns: new[] { "ParentTaskId", "ScheduledExecutionUtc" },
                // No filter needed: PostgreSQL treats NULLs as distinct in a unique index, so the ordinary
                // rows (all with a null ParentTaskId) never collide with each other.
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_QueuedTasks_OccurrenceSlot",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                sql: "\"ParentTaskId\" IS NULL OR \"ScheduledExecutionUtc\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_QueuedTasks_QueuedTasks_ParentTaskId",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                column: "ParentTaskId",
                principalSchema: _dbContext.Schema,
                principalTable: "QueuedTasks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var schema = string.IsNullOrEmpty(_dbContext.Schema) ? "public" : _dbContext.Schema;

            migrationBuilder.Sql($"DELETE FROM \"{schema}\".\"QueuedTasks\" WHERE \"ParentTaskId\" IS NOT NULL");

            migrationBuilder.DropForeignKey(
                name: "FK_QueuedTasks_QueuedTasks_ParentTaskId",
                schema: _dbContext.Schema,
                table: "QueuedTasks");

            migrationBuilder.DropIndex(
                name: "IX_QueuedTasks_ParentTaskId",
                schema: _dbContext.Schema,
                table: "QueuedTasks");

            migrationBuilder.DropIndex(
                name: "IX_QueuedTasks_ParentTaskId_Status",
                schema: _dbContext.Schema,
                table: "QueuedTasks");

            migrationBuilder.DropIndex(
                name: "UX_QueuedTasks_Occurrence",
                schema: _dbContext.Schema,
                table: "QueuedTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_QueuedTasks_OccurrenceSlot",
                schema: _dbContext.Schema,
                table: "QueuedTasks");

            migrationBuilder.DropColumn(
                name: "ParentTaskId",
                schema: _dbContext.Schema,
                table: "QueuedTasks");

            migrationBuilder.DropColumn(
                name: "RuntimeInfo",
                schema: _dbContext.Schema,
                table: "QueuedTasks");

            migrationBuilder.DropColumn(
                name: "ScheduleVersion",
                schema: _dbContext.Schema,
                table: "QueuedTasks");
        }
    }
}
