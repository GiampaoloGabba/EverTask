using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EverTask.Storage.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskKey : Migration
    {
        private readonly ITaskStoreDbContext _dbContext;

        public AddTaskKey(ITaskStoreDbContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TaskKey",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_QueuedTasks_TaskKey",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                column: "TaskKey",
                unique: true,
                filter: "[TaskKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_QueuedTasks_TaskKey",
                schema: _dbContext.Schema,
                table: "QueuedTasks");

            migrationBuilder.DropColumn(
                name: "TaskKey",
                schema: _dbContext.Schema,
                table: "QueuedTasks");
        }
    }
}
