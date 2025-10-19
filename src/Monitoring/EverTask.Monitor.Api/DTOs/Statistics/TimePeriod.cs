namespace EverTask.Monitor.Api.DTOs.Statistics;

/// <summary>
/// Predefined time periods for trend analysis.
/// </summary>
public enum TimePeriod
{
    /// <summary>
    /// Last 7 days with daily intervals
    /// </summary>
    Last7Days,

    /// <summary>
    /// Last 30 days with daily intervals
    /// </summary>
    Last30Days,

    /// <summary>
    /// Last 90 days with weekly intervals
    /// </summary>
    Last90Days
}
