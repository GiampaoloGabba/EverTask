namespace EverTask.Monitor.Api.DTOs.Dashboard;

/// <summary>
/// Predefined date ranges for filtering.
/// </summary>
public enum DateRange
{
    /// <summary>
    /// Today (from start of current day)
    /// </summary>
    Today,

    /// <summary>
    /// Last 7 days
    /// </summary>
    Week,

    /// <summary>
    /// Last 30 days
    /// </summary>
    Month,

    /// <summary>
    /// All available data
    /// </summary>
    All
}
