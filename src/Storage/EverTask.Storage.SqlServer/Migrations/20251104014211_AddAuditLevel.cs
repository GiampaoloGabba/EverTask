using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EverTask.Storage.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLevel : Migration
    {
        private readonly ITaskStoreDbContext _dbContext;

        public AddAuditLevel(ITaskStoreDbContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AuditLevel",
                schema: _dbContext.Schema,
                table: "QueuedTasks",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuditLevel",
                schema: _dbContext.Schema,
                table: "QueuedTasks");
        }
    }
}
