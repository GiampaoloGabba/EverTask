using EverTask.Storage;

namespace EverTask.Monitor.Api.DTOs.Tasks;

/// <summary>
/// Complete task information including audits and serialized request.
/// </summary>
public record TaskDetailDto(
    Guid Id,
    string Type,                          // Full type name
    string Handler,                       // Full handler type name
    string Request,                       // JSON serialized request
    QueuedTaskStatus Status,
    string? QueueName,
    string? TaskKey,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastExecutionUtc,
    DateTimeOffset? ScheduledExecutionUtc,
    string? Exception,                    // Last exception if any
    bool IsRecurring,
    string? RecurringTask,                // JSON serialized recurring config
    string? RecurringInfo,                // Human-readable schedule
    int? CurrentRunCount,
    int? MaxRuns,
    DateTimeOffset? RunUntil,
    DateTimeOffset? NextRunUtc,
    int? AuditLevel,                      // Audit retention policy level
    List<StatusAuditDto> StatusAudits,
    List<RunsAuditDto> RunsAudits
);
