namespace EverTask.Storage.Postgres;

public class PostgresTaskStoreContext(DbContextOptions<PostgresTaskStoreContext> options)
    : TaskStoreEfDbContext<PostgresTaskStoreContext>(options)
{
    /// <inheritdoc />
    /// <remarks>
    /// PostgreSQL folds unquoted identifiers to lower case, and EF emits the columns quoted and mixed-case,
    /// so the portable unquoted form of the base would reference columns that do not exist.
    /// </remarks>
    protected override string OccurrenceSlotCheckSql =>
        "\"ParentTaskId\" IS NULL OR \"ScheduledExecutionUtc\" IS NOT NULL";
}
