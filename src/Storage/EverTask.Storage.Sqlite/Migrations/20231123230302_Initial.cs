using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EverTask.Storage.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QueuedTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastExecutionUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ScheduledExecutionUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Type = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Request = table.Column<string>(type: "TEXT", nullable: false),
                    Handler = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Exception = table.Column<string>(type: "TEXT", nullable: true),
                    IsRecurring = table.Column<bool>(type: "INTEGER", nullable: false),
                    RecurringTask = table.Column<string>(type: "TEXT", nullable: true),
                    RecurringInfo = table.Column<string>(type: "TEXT", nullable: true),
                    CurrentRunCount = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxRuns = table.Column<int>(type: "INTEGER", nullable: true),
                    NextRunUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 15, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueuedTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RunsAudit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    QueuedTaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExecutedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 15, nullable: false),
                    Exception = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunsAudit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunsAudit_QueuedTasks_QueuedTaskId",
                        column: x => x.QueuedTaskId,
                        principalTable: "QueuedTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StatusAudit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    QueuedTaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    NewStatus = table.Column<string>(type: "TEXT", maxLength: 15, nullable: false),
                    Exception = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusAudit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StatusAudit_QueuedTasks_QueuedTaskId",
                        column: x => x.QueuedTaskId,
                        principalTable: "QueuedTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QueuedTasks_Status",
                table: "QueuedTasks",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_RunsAudit_QueuedTaskId",
                table: "RunsAudit",
                column: "QueuedTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_StatusAudit_QueuedTaskId",
                table: "StatusAudit",
                column: "QueuedTaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RunsAudit");

            migrationBuilder.DropTable(
                name: "StatusAudit");

            migrationBuilder.DropTable(
                name: "QueuedTasks");
        }
    }
}
