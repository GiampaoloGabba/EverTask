using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EverTask.Storage.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddRecoveryDispatchFailureCount : Migration
    {
        private readonly ITaskStoreDbContext _dbContext;

        public AddRecoveryDispatchFailureCount(ITaskStoreDbContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RecoveryDispatchFailureCount",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecoveryDispatchFailureCount",
                schema: _dbContext.Schema,
                table: "QueuedTasks");
        }
    }
}
