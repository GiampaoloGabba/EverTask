using System;
using EverTask.Storage.EfCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EverTask.Storage.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        private readonly ITaskStoreDbContext _dbContext;

        public Initial(ITaskStoreDbContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            TargetModel.GetDefaultSchema();

            if (!string.IsNullOrEmpty(_dbContext.Schema))
                migrationBuilder.EnsureSchema(name: _dbContext.Schema);

            migrationBuilder.CreateTable(
                name: "QueuedTasks",
                schema: _dbContext.Schema,
                columns: table => new
                {
                    Id               = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc     = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastExecutionUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Type             = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Handler          = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false, defaultValue: ""),
                    Request          = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValue: ""),
                    Exception        = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status           = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueuedTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QueuedTaskStatusAudit",
                schema: _dbContext.Schema,
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    QueuedTaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NewStatus = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    Exception = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueuedTaskStatusAudit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QueuedTaskStatusAudit_QueuedTasks_QueuedTaskId",
                        column: x => x.QueuedTaskId,
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
                name: "IX_QueuedTaskStatusAudit_QueuedTaskId",
                schema: _dbContext.Schema,
                table: "QueuedTaskStatusAudit",
                column: "QueuedTaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QueuedTaskStatusAudit",
                schema: _dbContext.Schema);

            migrationBuilder.DropTable(
                name: "QueuedTasks",
                schema: _dbContext.Schema);
        }
    }
}
