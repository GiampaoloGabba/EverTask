using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EverTask.Storage.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLevel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AuditLevel",
                table: "QueuedTasks",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuditLevel",
                table: "QueuedTasks");
        }
    }
}
