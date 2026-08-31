namespace EverTask.Monitor.Api.DTOs.Tasks;

/// <summary>
/// Paginated response for the occurrences of a durable schedule, newest slot first.
/// </summary>
/// <param name="Occurrences">The occurrences in the current response.</param>
/// <param name="TotalCount">Total number of occurrences matching the request.</param>
/// <param name="Skip">Number of occurrences skipped.</param>
/// <param name="Take">Number of occurrences returned.</param>
public record OccurrencesResponse(
    List<OccurrenceDto> Occurrences,
    int TotalCount,
    int Skip,
    int Take
);
