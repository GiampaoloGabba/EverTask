using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EverTask.Storage.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddQueueName : Migration
    {
        private readonly ITaskStoreDbContext _dbContext;

        public AddQueueName(ITaskStoreDbContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "QueueName",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QueueName",
                schema: _dbContext.Schema,
                table: "QueuedTasks");
        }
    }
}
