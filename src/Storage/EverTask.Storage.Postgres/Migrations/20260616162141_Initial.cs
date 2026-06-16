using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EverTask.Storage.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        // Schema-aware migration (Option B = full parity with SQL Server): the configured schema is injected
        // at runtime via DbSchemaAwareMigrationAssembly, so a SchemaName override is honored without
        // regenerating the migration. The design-time scaffold baked "evertask"; this hand-edit replaces it
        // with _dbContext.Schema everywhere (null/empty => default "public").
        private readonly ITaskStoreDbContext _dbContext;

        public Initial(ITaskStoreDbContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (!string.IsNullOrEmpty(_dbContext.Schema))
                migrationBuilder.EnsureSchema(name: _dbContext.Schema);

            migrationBuilder.CreateTable(
                name: "QueuedTasks",
                schema: _dbContext.Schema,
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastExecutionUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExecutionTimeMs = table.Column<double>(type: "double precision", nullable: false),
                    ScheduledExecutionUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Type = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Request = table.Column<string>(type: "text", nullable: false),
                    Handler = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Exception = table.Column<string>(type: "text", nullable: true),
                    IsRecurring = table.Column<bool>(type: "boolean", nullable: false),
                    RecurringTask = table.Column<string>(type: "text", nullable: true),
                    RecurringInfo = table.Column<string>(type: "text", nullable: true),
                    CurrentRunCount = table.Column<int>(type: "integer", nullable: true),
                    MaxRuns = table.Column<int>(type: "integer", nullable: true),
                    RunUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextRunUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    QueueName = table.Column<string>(type: "text", nullable: true),
                    TaskKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AuditLevel = table.Column<int>(type: "integer", nullable: true),
                    RecoveryDispatchFailureCount = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueuedTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RunsAudit",
                schema: _dbContext.Schema,
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    QueuedTaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExecutionTimeMs = table.Column<double>(type: "double precision", nullable: false),
                    Status = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    Exception = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunsAudit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunsAudit_QueuedTasks_QueuedTaskId",
                        column: x => x.QueuedTaskId,
                        principalSchema: _dbContext.Schema,
                        principalTable: "QueuedTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StatusAudit",
                schema: _dbContext.Schema,
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    QueuedTaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NewStatus = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    Exception = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusAudit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StatusAudit_QueuedTasks_QueuedTaskId",
                        column: x => x.QueuedTaskId,
                        principalSchema: _dbContext.Schema,
                        principalTable: "QueuedTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskExecutionLogs",
                schema: _dbContext.Schema,
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ExceptionDetails = table.Column<string>(type: "text", nullable: true),
                    SequenceNumber = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskExecutionLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskExecutionLogs_QueuedTasks_TaskId",
                        column: x => x.TaskId,
                        principalSchema: _dbContext.Schema,
                        principalTable: "QueuedTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QueuedTasks_Status",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_QueuedTasks_TaskKey",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                column: "TaskKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RunsAudit_QueuedTaskId",
                schema: _dbContext.Schema,
                table: "RunsAudit",
                column: "QueuedTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_StatusAudit_QueuedTaskId",
                schema: _dbContext.Schema,
                table: "StatusAudit",
                column: "QueuedTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskExecutionLogs_TaskId_TimestampUtc",
                schema: _dbContext.Schema,
                table: "TaskExecutionLogs",
                columns: new[] { "TaskId", "TimestampUtc" });

            // Recovery index (FORM B = partial + covering): keyed on (CreatedAtUtc, Id) to serve the keyset
            // ORDER BY of RetrievePending; INCLUDE covers the runtime predicate eval (RunUntil/MaxRuns/...);
            // the STATIC partial WHERE prunes the bulk of terminal rows (Completed/Failed non-recurring), which
            // a SQL-Server-style non-filtered covering index would not. NO now() in the predicate (mutable =>
            // non-deterministic index): RunUntil >= now stays a runtime filter. Raw SQL because EF's CreateIndex
            // cannot express a filtered+INCLUDE index portably; schema interpolated with a "public" fallback.
            var recoverySchema = string.IsNullOrEmpty(_dbContext.Schema) ? "public" : _dbContext.Schema;
            migrationBuilder.Sql($@"
                CREATE INDEX ""IX_QueuedTasks_Recovery""
                ON ""{recoverySchema}"".""QueuedTasks"" (""CreatedAtUtc"", ""Id"")
                INCLUDE (""Status"", ""IsRecurring"", ""NextRunUtc"", ""MaxRuns"", ""CurrentRunCount"", ""RunUntil"")
                WHERE ""Status"" IN ('WaitingQueue', 'Queued', 'Pending', 'ServiceStopped', 'InProgress')
                   OR (""IsRecurring"" = true AND ""NextRunUtc"" IS NOT NULL);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var recoverySchema = string.IsNullOrEmpty(_dbContext.Schema) ? "public" : _dbContext.Schema;
            migrationBuilder.Sql($@"DROP INDEX IF EXISTS ""{recoverySchema}"".""IX_QueuedTasks_Recovery"";");

            migrationBuilder.DropTable(
                name: "RunsAudit",
                schema: _dbContext.Schema);

            migrationBuilder.DropTable(
                name: "StatusAudit",
                schema: _dbContext.Schema);

            migrationBuilder.DropTable(
                name: "TaskExecutionLogs",
                schema: _dbContext.Schema);

            migrationBuilder.DropTable(
                name: "QueuedTasks",
                schema: _dbContext.Schema);
        }
    }
}
