using EverTask.Abstractions;
using EverTask.Monitor.Api.DTOs.Tasks;
using EverTask.RateLimiting;
using EverTask.Storage;

namespace EverTask.Monitor.Api.Services;

/// <summary>
/// Service for querying tasks from storage.
/// </summary>
/// <param name="storage">The task storage.</param>
/// <param name="rateLimiter">
/// Optional rate-limiter introspection: sources the per-task <c>throttledUntil</c> overlay
/// (in-memory join, single-node).
/// </param>
public class TaskQueryService(ITaskStorage storage, IRateLimiterIntrospection? rateLimiter = null) : ITaskQueryService
{
    private const int MaximumPageSize = 500;

    /// <inheritdoc />
    public async Task<TasksPagedResponse> GetTasksAsync(TaskFilter filter, PaginationParams pagination, CancellationToken ct = default)
    {
        // Get all tasks from storage
        var allTasks = await storage.GetAll(ct).ConfigureAwait(false);
        var query = allTasks.AsQueryable();

        // Apply filters
        if (filter.Statuses is { Count: > 0 })
        {
            query = query.Where(t => filter.Statuses.Contains(t.Status));
        }

        if (!string.IsNullOrWhiteSpace(filter.TaskType))
        {
            query = query.Where(t => t.Type.Contains(filter.TaskType, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filter.QueueName))
        {
            query = query.Where(t => t.QueueName == filter.QueueName);
        }

        if (filter.IsRecurring.HasValue)
        {
            query = query.Where(t => t.IsRecurring == filter.IsRecurring.Value);
        }

        if (filter.CreatedAfter.HasValue)
        {
            query = query.Where(t => t.CreatedAtUtc >= filter.CreatedAfter.Value);
        }

        if (filter.CreatedBefore.HasValue)
        {
            query = query.Where(t => t.CreatedAtUtc <= filter.CreatedBefore.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.SearchTerm))
        {
            var searchLower = filter.SearchTerm.ToLower();
            query = query.Where(t =>
                t.Type.ToLower().Contains(searchLower) ||
                t.Handler.ToLower().Contains(searchLower) ||
                (t.TaskKey != null && t.TaskKey.ToLower().Contains(searchLower)));
        }

        if (filter.ParentTaskId.HasValue)
        {
            query = query.Where(t => t.ParentTaskId == filter.ParentTaskId.Value);
        }

        if (filter.OnlyOccurrences.HasValue)
        {
            query = filter.OnlyOccurrences.Value
                        ? query.Where(t => t.ParentTaskId != null)
                        : query.Where(t => t.ParentTaskId == null);
        }

        if (filter.OnlyCatchUp.HasValue)
        {
            // Only an occurrence can stand for missed work, and which kind it stands for lives in that row's
            // runtime JSON: the column test short-circuits the parse, so it runs over the occurrence rows
            // and never over the whole store.
            query = filter.OnlyCatchUp.Value
                        ? query.Where(t => t.ParentTaskId != null && StandsForMissedWork(t))
                        : query.Where(t => t.ParentTaskId == null || !StandsForMissedWork(t));
        }

        // Count total before pagination
        var totalCount = query.Count();

        // Apply sorting
        query = ApplySorting(query, pagination.SortBy, pagination.SortDescending);

        // Apply pagination
        var skip = (pagination.Page - 1) * pagination.PageSize;
        var page = query
            .Skip(skip)
            .Take(pagination.PageSize)
            .ToList(); // project in memory: the throttledUntil overlay is an in-memory join

        // One query for the whole page: when a run STARTED is recorded in the audit trail, not in a column.
        var starts = await TaskRunTiming.RecordedStartsAsync(storage, page, ct).ConfigureAwait(false);

        var items = page.Select(t => ToListDto(t, starts)).ToList();

        var totalPages = (int)Math.Ceiling(totalCount / (double)pagination.PageSize);

        return new TasksPagedResponse(items, totalCount, pagination.Page, pagination.PageSize, totalPages);
    }

    /// <inheritdoc />
    public async Task<TaskDetailDto?> GetTaskDetailAsync(Guid id, CancellationToken ct = default)
    {
        var tasks = await storage.Get(t => t.Id == id, ct).ConfigureAwait(false);
        var task = tasks.FirstOrDefault();

        if (task == null)
            return null;

        // The FIRST page of each trail, not the whole of it: the two blocks answer the same reads as the two
        // endpoints, and a schedule that has run for a year holds a transition per state per run. The totals
        // travel with them so a consumer knows there is more and where to ask for it.
        var statusAudits = await ReadStatusAuditsAsync(id, 0, ITaskQueryService.DefaultAuditPageSize, ct).ConfigureAwait(false);
        var runsAudits   = await ReadRunsAuditsAsync(id, 0, ITaskQueryService.DefaultAuditPageSize, ct).ConfigureAwait(false);

        var facts  = TaskScheduleFacts.Read(task);
        var starts = await TaskRunTiming.RecordedStartsAsync(storage, [task], ct).ConfigureAwait(false);

        return new TaskDetailDto(
            task.Id,
            task.Type,
            task.Handler,
            task.Request,
            task.Status,
            task.QueueName,
            task.TaskKey,
            task.CreatedAtUtc,
            task.LastExecutionUtc,
            task.ScheduledExecutionUtc,
            task.Exception,
            task.IsRecurring,
            task.RecurringTask,
            task.RecurringInfo,
            task.CurrentRunCount,
            task.MaxRuns,
            task.RunUntil,
            task.NextRunUtc,
            task.AuditLevel,
            task.ExecutionTimeMs,
            statusAudits.Audits,
            runsAudits.Audits,
            rateLimiter?.GetThrottledUntil(task.Id)
        )
        {
            StatusAuditsTotalCount = statusAudits.TotalCount,
            RunsAuditsTotalCount   = runsAudits.TotalCount,
            ParentTaskId           = task.ParentTaskId,
            OccurrenceMode         = facts.OccurrenceMode,
            MisfirePolicy          = facts.MisfirePolicy,
            TimeZoneId             = facts.TimeZoneId,
            ScheduleVersion        = ScheduleVersionOf(task),
            NominalSlotUtc         = facts.NominalSlotUtc,
            StartedAtUtc           = TaskRunTiming.StartOfLastRun(task, starts),
            MisfireKind            = facts.MisfireKind,
            Occurrence             = facts.Occurrence,
            Halt                   = facts.Halt
        };
    }

    /// <inheritdoc />
    public Task<StatusAuditsResponse> GetStatusAuditAsync(Guid id, int skip = 0,
                                                          int take = ITaskQueryService.DefaultAuditPageSize,
                                                          CancellationToken ct = default) =>
        ReadStatusAuditsAsync(id, skip, take, ct);

    /// <inheritdoc />
    public Task<RunsAuditsResponse> GetRunsAuditAsync(Guid id, int skip = 0, int take = ITaskQueryService.DefaultAuditPageSize,
                                                      CancellationToken ct = default) =>
        ReadRunsAuditsAsync(id, skip, take, ct);

    /// <summary>
    /// One page of the row's status transitions, newest first, READ from the storage.
    /// </summary>
    /// <remarks>
    /// Never off <see cref="QueuedTask.StatusAudits"/>: no storage read populates that navigation, so walking
    /// it here answered the whole history over the in-memory store and an empty list over every relational
    /// one — for a row whose audit table holds every transition it ever made.
    /// <para>
    /// The PAGE is the storage read, ordering and counting included, for the same reason as
    /// <c>GetOccurrencesAsync</c>: the trail of a long-lived schedule holds one transition per state per run,
    /// and slicing it here would transfer every one of them to show twenty.
    /// </para>
    /// </remarks>
    private async Task<StatusAuditsResponse> ReadStatusAuditsAsync(Guid id, int skip, int take,
                                                                   CancellationToken ct)
    {
        (skip, take) = ClampPage(skip, take);

        var page = await storage.GetStatusAuditsPage(id, skip, take, ct).ConfigureAwait(false);

        var audits = page.Audits
                         .Select(a => new StatusAuditDto(a.Id, a.QueuedTaskId, a.UpdatedAtUtc, a.NewStatus,
                             a.Exception))
                         .ToList();

        return new StatusAuditsResponse(audits, page.TotalCount, skip, take);
    }

    /// <summary>One page of the row's runs, newest first, read from the storage for the same reason.</summary>
    private async Task<RunsAuditsResponse> ReadRunsAuditsAsync(Guid id, int skip, int take, CancellationToken ct)
    {
        (skip, take) = ClampPage(skip, take);

        var page = await storage.GetRunsAuditsPage(id, skip, take, ct).ConfigureAwait(false);

        var audits = page.Audits
                         .Select(a => new RunsAuditDto(a.Id, a.QueuedTaskId, a.ExecutedAt, a.ExecutionTimeMs,
                             a.Status, a.Exception))
                         .ToList();

        return new RunsAuditsResponse(audits, page.TotalCount, skip, take);
    }

    /// <summary>
    /// Query-string values are clamped before they reach storage: negative offsets break OFFSET / FETCH, and
    /// an unbounded page lets one request amplify both the indexed read and its response.
    /// </summary>
    private static (int Skip, int Take) ClampPage(int skip, int take) =>
        (Math.Max(0, skip), Math.Min(MaximumPageSize, Math.Max(0, take)));

    /// <inheritdoc />
    public async Task<ExecutionLogsResponse> GetExecutionLogsAsync(Guid taskId, int skip = 0, int take = 100, string? levelFilter = null, CancellationToken ct = default)
    {
        (skip, take) = ClampPage(skip, take);

        // Get all logs for the task
        var allLogs = await storage.GetExecutionLogsAsync(taskId, ct).ConfigureAwait(false);

        // Apply level filter if specified. Materialized once: the count and the page below both
        // enumerate it, and re-running the predicate per enumeration is pure waste.
        IReadOnlyList<TaskExecutionLog> filteredLogs = string.IsNullOrWhiteSpace(levelFilter)
                                                           ? allLogs
                                                           : [.. allLogs.Where(l => l.Level.Equals(levelFilter,
                                                               StringComparison.OrdinalIgnoreCase))];

        var totalCount = filteredLogs.Count;

        // Apply pagination and map to DTOs
        var logs = filteredLogs
            .Skip(skip)
            .Take(take)
            .Select(l => new ExecutionLogDto(
                l.Id,
                l.TimestampUtc,
                l.Level,
                l.Message,
                l.ExceptionDetails,
                l.SequenceNumber
            ))
            .ToList();

        return new ExecutionLogsResponse(logs, totalCount, skip, take);
    }

    /// <inheritdoc />
    public async Task<TaskCountsDto> GetTaskCountsAsync(CancellationToken ct = default)
    {
        // The standard/recurring split needs IsRecurring, which ITaskStorageStatistics does not
        // expose: the list is materialized once and every count derives from it (a separate
        // statistics roundtrip would be strictly more work on top of the same materialization).
        var allTasksList = (await storage.GetAll(ct).ConfigureAwait(false)).ToList();

        var all       = allTasksList.Count;
        var recurring = allTasksList.Count(t => t.IsRecurring);
        var standard  = all - recurring;
        var failed    = allTasksList.Count(t => t.Status == QueuedTaskStatus.Failed);

        // An occurrence is a one-shot row, so it is already inside Standard: this is the slice of it that a
        // durable schedule produced, not a sixth disjoint bucket.
        var occurrences = allTasksList.Count(t => t.ParentTaskId != null);

        return new TaskCountsDto(all, standard, recurring, failed) { Occurrences = occurrences };
    }

    /// <inheritdoc />
    public async Task<OccurrencesResponse> GetOccurrencesAsync(Guid scheduleId, bool nonTerminalOnly = false,
                                                               int skip = 0, int take = 100,
                                                               CancellationToken ct = default)
    {
        (skip, take) = ClampPage(skip, take);

        // The PAGE is the storage read, ordering and counting included: a schedule with a year of retention
        // behind it holds hundreds of thousands of occurrence rows, and slicing them here would mean
        // transferring and sorting every one of them to show a hundred. The built-in providers answer it from
        // the (ParentTaskId, ScheduledExecutionUtc) index the occurrence contract already needs.
        var page = await storage.GetOccurrencesPage(scheduleId, nonTerminalOnly, skip, take, ct)
                                 .ConfigureAwait(false);

        var starts = await TaskRunTiming.RecordedStartsAsync(storage, page.Occurrences, ct)
                                        .ConfigureAwait(false);

        var occurrences = page.Occurrences
            .Select(r => new OccurrenceDto(
                r.Id,
                scheduleId,
                r.Status,
                TaskScheduleFacts.Read(r).Occurrence ?? UnreadableOccurrence,
                r.CreatedAtUtc,
                r.LastExecutionUtc,
                r.ExecutionTimeMs,
                r.Exception,
                r.ScheduleVersion)
            {
                StartedAtUtc = TaskRunTiming.StartOfLastRun(r, starts)
            })
            .ToList();

        return new OccurrencesResponse(occurrences, page.TotalCount, skip, take);
    }

    /// <summary>
    /// What an occurrence reports when the row carries no readable metadata at all. Read returns one for every
    /// row whose ParentTaskId is set, so this is the answer to a row that reached the endpoint without being
    /// an occurrence — which the storage read cannot produce.
    /// </summary>
    private static readonly OccurrenceInfoDto UnreadableOccurrence =
        new(null, null, null, null, null, null, null, null);

    /// <summary>
    /// True while the row's occurrence metadata says it was created out of missed work — a replayed slot, or a
    /// run of missed slots collapsed into one.
    /// </summary>
    private static bool StandsForMissedWork(QueuedTask row) =>
        TaskScheduleFacts.Read(row).MisfireKind is MisfireKind.CatchUp or MisfireKind.FireOnce;

    private TaskListDto ToListDto(QueuedTask t, IReadOnlyDictionary<Guid, DateTimeOffset> recordedStarts)
    {
        var facts = TaskScheduleFacts.Read(t);

        return new TaskListDto(
            t.Id,
            GetShortTypeName(t.Type),
            t.Status,
            t.QueueName,
            t.TaskKey,
            t.CreatedAtUtc,
            t.LastExecutionUtc,
            t.ScheduledExecutionUtc,
            t.IsRecurring,
            t.RecurringInfo,
            t.CurrentRunCount,
            t.MaxRuns,
            t.ExecutionTimeMs,
            rateLimiter?.GetThrottledUntil(t.Id)
        )
        {
            ParentTaskId    = t.ParentTaskId,
            OccurrenceMode  = facts.OccurrenceMode,
            MisfirePolicy   = facts.MisfirePolicy,
            TimeZoneId      = facts.TimeZoneId,
            ScheduleVersion = ScheduleVersionOf(t),
            NominalSlotUtc  = facts.NominalSlotUtc,
            StartedAtUtc    = TaskRunTiming.StartOfLastRun(t, recordedStarts),
            MisfireKind     = facts.MisfireKind
        };
    }

    /// <summary>
    /// The schedule version of a row that belongs to a schedule, and nothing at all for a row that does not.
    /// </summary>
    /// <remarks>
    /// The column exists on every row and defaults to 0, but a plain one-shot belongs to no schedule and no
    /// version of one: reporting 0 there made "does this row carry schedule fields" answer yes for every task
    /// in the store, which is exactly what a consumer uses those fields to tell apart.
    /// </remarks>
    private static int? ScheduleVersionOf(QueuedTask row) =>
        row.IsRecurring || row.ParentTaskId != null ? row.ScheduleVersion : null;

    private static IQueryable<QueuedTask> ApplySorting(IQueryable<QueuedTask> query, string? sortBy, bool descending)
    {
        if (string.IsNullOrWhiteSpace(sortBy))
            sortBy = "CreatedAtUtc";

        return sortBy switch
        {
            "CreatedAtUtc" => descending ? query.OrderByDescending(t => t.CreatedAtUtc) : query.OrderBy(t => t.CreatedAtUtc),
            "LastExecutionUtc" => descending ? query.OrderByDescending(t => t.LastExecutionUtc) : query.OrderBy(t => t.LastExecutionUtc),
            "ScheduledExecutionUtc" => descending ? query.OrderByDescending(t => t.ScheduledExecutionUtc) : query.OrderBy(t => t.ScheduledExecutionUtc),
            "Status" => descending ? query.OrderByDescending(t => t.Status) : query.OrderBy(t => t.Status),
            "Type" => descending ? query.OrderByDescending(t => t.Type) : query.OrderBy(t => t.Type),
            "QueueName" => descending ? query.OrderByDescending(t => t.QueueName) : query.OrderBy(t => t.QueueName),
            _ => descending ? query.OrderByDescending(t => t.CreatedAtUtc) : query.OrderBy(t => t.CreatedAtUtc)
        };
    }

    private static string GetShortTypeName(string fullTypeName)
    {
        if (string.IsNullOrWhiteSpace(fullTypeName))
            return string.Empty;

        // Try to get just the class name without assembly qualification
        var type = Type.GetType(fullTypeName);
        if (type != null)
            return type.Name;

        // Fallback: extract from string (format is "Namespace.ClassName, AssemblyName")
        var commaIndex = fullTypeName.IndexOf(',');
        var typeName = commaIndex > 0 ? fullTypeName[..commaIndex] : fullTypeName;
        var lastDotIndex = typeName.LastIndexOf('.');
        return lastDotIndex > 0 ? typeName[(lastDotIndex + 1)..] : typeName;
    }
}
