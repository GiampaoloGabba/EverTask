using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EverTask.Storage.MySql.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "QueuedTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    LastExecutionUtc = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: true),
                    ExecutionTimeMs = table.Column<double>(type: "double", nullable: false),
                    ScheduledExecutionUtc = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: true),
                    Type = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Request = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Handler = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Exception = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IsRecurring = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    RecurringTask = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RecurringInfo = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CurrentRunCount = table.Column<int>(type: "int", nullable: true),
                    MaxRuns = table.Column<int>(type: "int", nullable: true),
                    RunUntil = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: true),
                    NextRunUtc = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: true),
                    QueueName = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TaskKey = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AuditLevel = table.Column<int>(type: "int", nullable: true),
                    RecoveryDispatchFailureCount = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "varchar(15)", maxLength: 15, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueuedTasks", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RunsAudit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    QueuedTaskId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ExecutedAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    ExecutionTimeMs = table.Column<double>(type: "double", nullable: false),
                    Status = table.Column<string>(type: "varchar(15)", maxLength: 15, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Exception = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
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
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "StatusAudit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    QueuedTaskId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    NewStatus = table.Column<string>(type: "varchar(15)", maxLength: 15, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Exception = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
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
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "TaskExecutionLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    TaskId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    Level = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Message = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ExceptionDetails = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SequenceNumber = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskExecutionLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskExecutionLogs_QueuedTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "QueuedTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_QueuedTasks_Status",
                table: "QueuedTasks",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_QueuedTasks_TaskKey",
                table: "QueuedTasks",
                column: "TaskKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RunsAudit_QueuedTaskId",
                table: "RunsAudit",
                column: "QueuedTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_StatusAudit_QueuedTaskId",
                table: "StatusAudit",
                column: "QueuedTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskExecutionLogs_TaskId_TimestampUtc",
                table: "TaskExecutionLogs",
                columns: new[] { "TaskId", "TimestampUtc" });

            // Recovery index for the startup-recovery query (RetrievePending): keyed on (CreatedAtUtc, Id) to
            // serve the keyset ORDER BY without a filesort on large tables. Unlike SQL Server (covering INCLUDE)
            // and PostgreSQL (partial WHERE), MySQL/MariaDB support NEITHER INCLUDE columns NOR partial/filtered
            // indexes, so this is a plain composite: it provides the ordering but does NOT prune terminal rows —
            // the recoverable-status predicate stays a runtime filter. Hand-added (kept out of the model), like
            // the Postgres recovery index.
            migrationBuilder.CreateIndex(
                name: "IX_QueuedTasks_Recovery",
                table: "QueuedTasks",
                columns: new[] { "CreatedAtUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_QueuedTasks_Recovery",
                table: "QueuedTasks");

            migrationBuilder.DropTable(
                name: "RunsAudit");

            migrationBuilder.DropTable(
                name: "StatusAudit");

            migrationBuilder.DropTable(
                name: "TaskExecutionLogs");

            migrationBuilder.DropTable(
                name: "QueuedTasks");
        }
    }
}
