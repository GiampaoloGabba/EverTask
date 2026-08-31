namespace EverTask.Storage.EfCore;

// Single DbContextOptions<T> constructor -> pool-compatible (AddPooledDbContextFactory). The schema can no
// longer be injected as a ctor dependency; it travels inside the options via EverTaskSchemaExtension and is
// read back here. OnModelCreating + the schema-aware migrations keep reading the Schema property unchanged.
public abstract class TaskStoreEfDbContext<T>(DbContextOptions<T> options)
    : DbContext(options), ITaskStoreDbContext where T : DbContext
{
    public string? Schema { get; } = options.FindExtension<EverTaskSchemaExtension>()?.Schema;

    /// <summary>
    /// Body of the check constraint that keeps an occurrence from existing without its nominal slot.
    /// Unquoted identifiers, which SQL Server, SQLite and MySQL resolve case-insensitively; PostgreSQL folds
    /// them to lower case while EF emits quoted mixed-case names, so it overrides this with a quoted form.
    /// </summary>
    protected virtual string OccurrenceSlotCheckSql =>
        "ParentTaskId IS NULL OR ScheduledExecutionUtc IS NOT NULL";

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

        // Durable occurrences: a child row names its schedule, and the three constraints below are what make
        // "one row per slot" a database guarantee instead of an application convention.
        modelBuilder.Entity<QueuedTask>()
                    .HasOne(q => q.Parent)
                    .WithMany(q => q.Occurrences)
                    .HasForeignKey(q => q.ParentTaskId)
                    // Restrict, never cascade: SQL Server rejects a cascading self-reference outright, and
                    // without a foreign key a Remove(schedule) racing a materializer would leave orphans.
                    // Deleting a schedule deletes its occurrences explicitly, in the same transaction.
                    .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<QueuedTask>()
                    // Stable name: the materializer recognises a lost race by the CONSTRAINT that was
                    // violated, not by a provider's generic duplicate-key code.
                    .HasIndex(q => new { q.ParentTaskId, q.ScheduledExecutionUtc })
                    .IsUnique()
                    .HasDatabaseName("UX_QueuedTasks_Occurrence");

        // Every ordinary row has a null ParentTaskId, so the unique index above must not collapse them.
        // PostgreSQL, SQLite and MySQL treat nulls as distinct; SQL Server does not, and EF's convention
        // filters unique indexes over nullable columns there — which is exactly the filter needed.

        modelBuilder.Entity<QueuedTask>()
                    .HasIndex(q => q.ParentTaskId)
                    .HasDatabaseName("IX_QueuedTasks_ParentTaskId")
                    .IsUnique(false);

        modelBuilder.Entity<QueuedTask>()
                    .HasIndex(q => new { q.ParentTaskId, q.Status })
                    .HasDatabaseName("IX_QueuedTasks_ParentTaskId_Status")
                    .IsUnique(false);

        modelBuilder.Entity<QueuedTask>()
                    .ToTable(t => t.HasCheckConstraint("CK_QueuedTasks_OccurrenceSlot", OccurrenceSlotCheckSql));

        // The DEFAULT belongs to the MODEL, not just to the AddColumn of one migration: SQLite cannot ALTER a
        // foreign key or a check constraint in, so its migration rebuilds the table from the model and the
        // rebuilt column would come out NOT NULL with no default, diverging from the other three providers.
        // ValueGeneratedNever keeps EverTask writing the value itself, so the default only serves an outside
        // writer that omits the column.
        modelBuilder.Entity<QueuedTask>()
                    .Property(q => q.ScheduleVersion)
                    .HasDefaultValue(0)
                    .ValueGeneratedNever();

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

            // ExceptionDetails: No MaxLength = NVARCHAR(MAX) in SQL Server, TEXT in Sqlite
            entity.Property(e => e.ExceptionDetails);

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
