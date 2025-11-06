namespace EverTask.Monitor.Api.DTOs.Tasks;

/// <summary>
/// A single execution log entry captured during task execution.
/// </summary>
public record ExecutionLogDto(
    Guid Id,
    DateTimeOffset TimestampUtc,
    string Level,                     // "Trace", "Debug", "Information", "Warning", "Error", "Critical"
    string Message,
    string? ExceptionDetails,
    int SequenceNumber                // Preserves log order
);
