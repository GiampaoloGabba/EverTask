using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EverTask.Storage.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionTimeMs : Migration
    {
        private readonly ITaskStoreDbContext _dbContext;

        public AddExecutionTimeMs(ITaskStoreDbContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "ExecutionTimeMs",
                schema: _dbContext.Schema,
                table: "RunsAudit",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "ExecutionTimeMs",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                type: "float",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExecutionTimeMs",
                schema: _dbContext.Schema,
                table: "RunsAudit");

            migrationBuilder.DropColumn(
                name: "ExecutionTimeMs",
                schema: _dbContext.Schema,
                table: "QueuedTasks");
        }
    }
}
