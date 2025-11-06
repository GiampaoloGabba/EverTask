namespace EverTask.Monitor.Api.DTOs.Tasks;

/// <summary>
/// A single execution log entry captured during task execution.
/// </summary>
/// <param name="Id">The unique identifier of the log entry.</param>
/// <param name="TimestampUtc">When the log entry was created.</param>
/// <param name="Level">The log level (Trace, Debug, Information, Warning, Error, Critical).</param>
/// <param name="Message">The log message.</param>
/// <param name="ExceptionDetails">Exception details if applicable.</param>
/// <param name="SequenceNumber">Sequence number to preserve log order.</param>
public record ExecutionLogDto(
    Guid Id,
    DateTimeOffset TimestampUtc,
    string Level,
    string Message,
    string? ExceptionDetails,
    int SequenceNumber
);
