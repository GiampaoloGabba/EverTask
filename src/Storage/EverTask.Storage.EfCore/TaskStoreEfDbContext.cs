using Microsoft.Extensions.Options;

namespace EverTask.Storage.EfCore;

public abstract class TaskStoreEfDbContext<T>(
    DbContextOptions<T> options,
    IOptions<ITaskStoreOptions> storeOptions)
    : DbContext(options), ITaskStoreDbContext where T : DbContext
{
    public string? Schema { get; } = storeOptions.Value.SchemaName;

    public DbSet<QueuedTask>       QueuedTasks       => Set<QueuedTask>();
    public DbSet<StatusAudit>      StatusAudit       => Set<StatusAudit>();
    public DbSet<RunsAudit>        RunsAudit         => Set<RunsAudit>();
    public DbSet<TaskExecutionLog> TaskExecutionLogs => Set<TaskExecutionLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        if (!string.IsNullOrEmpty(Schema))
            modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<QueuedTask>()
                    .Property(e => e.Type)
                    .HasMaxLength(500)
                    .IsRequired();

        modelBuilder.Entity<QueuedTask>()
                    .Property(e => e.Request)
                    .IsRequired();

        modelBuilder.Entity<QueuedTask>()
                    .Property(e => e.Handler)
                    .HasMaxLength(500)
                    .IsRequired();

        modelBuilder.Entity<QueuedTask>()
                    .Property(e => e.Status)
                    .HasConversion<string>().HasMaxLength(15)
                    .IsRequired();

        modelBuilder.Entity<QueuedTask>()
                    .HasIndex(q => q.Status)
                    .IsUnique(false);

        modelBuilder.Entity<QueuedTask>()
                    .Property(e => e.TaskKey)
                    .HasMaxLength(200);

        modelBuilder.Entity<QueuedTask>()
                    .HasIndex(q => q.TaskKey)
                    .IsUnique();

        modelBuilder.Entity<QueuedTask>()
                    .HasMany(a => a.StatusAudits)
                    .WithOne(af => af.QueuedTask)
                    .HasForeignKey(af => af.QueuedTaskId)
                    .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<QueuedTask>()
                    .HasMany(a => a.RunsAudits)
                    .WithOne(af => af.QueuedTask)
                    .HasForeignKey(af => af.QueuedTaskId)
                    .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<StatusAudit>()
                    .HasIndex(q => q.QueuedTaskId)
                    .IsUnique(false);

        modelBuilder.Entity<StatusAudit>()
                    .Property(e => e.NewStatus)
                    .HasConversion<string>().HasMaxLength(15)
                    .IsRequired();

        modelBuilder.Entity<RunsAudit>()
                    .HasIndex(q => q.QueuedTaskId)
                    .IsUnique(false);

        modelBuilder.Entity<RunsAudit>()
                    .Property(e => e.Status)
                    .HasConversion<string>().HasMaxLength(15)
                    .IsRequired();

        // Configure TaskExecutionLog entity
        modelBuilder.Entity<TaskExecutionLog>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Level)
                .HasMaxLength(20)
                .IsRequired();

            entity.Property(e => e.Message)
                .HasMaxLength(4000)
                .IsRequired();

            entity.Property(e => e.ExceptionDetails)
                .HasMaxLength(-1); // SQL Server: NVARCHAR(MAX), Sqlite: TEXT

            entity.Property(e => e.TimestampUtc)
                .IsRequired();

            entity.Property(e => e.SequenceNumber)
                .IsRequired();

            // Index for efficient querying by TaskId and time
            entity.HasIndex(e => new { e.TaskId, e.TimestampUtc })
                .HasDatabaseName("IX_TaskExecutionLogs_TaskId_TimestampUtc");

            // Foreign key with cascade delete
            entity.HasOne(e => e.Task)
                .WithMany(t => t.ExecutionLogs)
                .HasForeignKey(e => e.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
