using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EverTask.Storage.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddQueueName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "QueueName",
                table: "QueuedTasks",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QueueName",
                table: "QueuedTasks");
        }
    }
}
