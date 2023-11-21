using System;
using EverTask.EfCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EverTask.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddRunsAudits : Migration
    {
        private readonly ITaskStoreDbContext _dbContext;

        public AddRunsAudits(ITaskStoreDbContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }
        
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            TargetModel.GetDefaultSchema();
            
            migrationBuilder.AddColumn<int>(
                name: "CurrentRunCount",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsRecurring",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MaxRuns",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextRunUtc",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecurringInfo",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecurringTask",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RunsAudit",
                schema: _dbContext.Schema,
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    QueuedTaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExecutedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    Exception = table.Column<string>(type: "nvarchar(max)", nullable: true)
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

            migrationBuilder.CreateIndex(
                name: "IX_RunsAudit_QueuedTaskId",
                schema: _dbContext.Schema,
                table: "RunsAudit",
                column: "QueuedTaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RunsAudit",
                schema: _dbContext.Schema);

            migrationBuilder.DropColumn(
                name: "CurrentRunCount",
                schema: _dbContext.Schema,
                table: "QueuedTasks");

            migrationBuilder.DropColumn(
                name: "IsRecurring",
                schema: _dbContext.Schema,
                table: "QueuedTasks");

            migrationBuilder.DropColumn(
                name: "MaxRuns",
                schema: _dbContext.Schema,
                table: "QueuedTasks");

            migrationBuilder.DropColumn(
                name: "NextRunUtc",
                schema: _dbContext.Schema,
                table: "QueuedTasks");

            migrationBuilder.DropColumn(
                name: "RecurringInfo",
                schema: _dbContext.Schema,
                table: "QueuedTasks");

            migrationBuilder.DropColumn(
                name: "RecurringTask",
                schema: _dbContext.Schema,
                table: "QueuedTasks");
        }
    }
}
