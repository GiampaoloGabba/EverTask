using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EverTask.Storage.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TaskKey",
                table: "QueuedTasks",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_QueuedTasks_TaskKey",
                table: "QueuedTasks",
                column: "TaskKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_QueuedTasks_TaskKey",
                table: "QueuedTasks");

            migrationBuilder.DropColumn(
                name: "TaskKey",
                table: "QueuedTasks");
        }
    }
}
