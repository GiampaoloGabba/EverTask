using System;
using EverTask.Storage.EfCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EverTask.Storage.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledDateTimeOffset : Migration
    {

        private readonly ITaskStoreDbContext _dbContext;

        public AddScheduledDateTimeOffset(ITaskStoreDbContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            TargetModel.GetDefaultSchema();

            if (!string.IsNullOrEmpty(_dbContext.Schema))
                migrationBuilder.EnsureSchema(name: _dbContext.Schema);

            migrationBuilder.AlterColumn<string>(
                name: "Request",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(500)",
                oldMaxLength: 500);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ScheduledExecutionUtc",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScheduledExecutionUtc",
                schema: _dbContext.Schema,
                table: "QueuedTasks");

            migrationBuilder.AlterColumn<string>(
                name: "Request",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");
        }
    }
}
