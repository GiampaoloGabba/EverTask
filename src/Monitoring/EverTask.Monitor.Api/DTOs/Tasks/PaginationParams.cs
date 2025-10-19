namespace EverTask.Monitor.Api.DTOs.Tasks;

/// <summary>
/// Pagination and sorting parameters.
/// </summary>
public class PaginationParams
{
    /// <summary>
    /// Page number (1-based)
    /// </summary>
    public int Page { get; set; } = 1;

    /// <summary>
    /// Number of items per page
    /// </summary>
    public int PageSize { get; set; } = 20;

    /// <summary>
    /// Property name to sort by
    /// </summary>
    public string? SortBy { get; set; } = "CreatedAtUtc";

    /// <summary>
    /// Sort in descending order
    /// </summary>
    public bool SortDescending { get; set; } = true;
}
