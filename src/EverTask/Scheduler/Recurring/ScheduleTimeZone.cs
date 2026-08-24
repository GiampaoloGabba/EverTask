using System.Diagnostics.CodeAnalysis;

namespace EverTask.Scheduler.Recurring;

/// <summary>
/// Resolution and normalization of the time zone id a schedule persists (T2).
/// </summary>
/// <remarks>
/// A schedule stores the id, never the <see cref="TimeZoneInfo"/>: the row outlives the process and the zone's
/// rules change under it, so freezing an offset (or a rule set) at dispatch is exactly the bug the zone support
/// exists to fix. The stored form is the IANA id, because that is the one both Windows and Linux resolve — a
/// Windows id written on Windows would not load on a Linux replica of the same deployment.
/// </remarks>
internal static class ScheduleTimeZone
{
    /// <summary>
    /// The persisted form of UTC. <see cref="TimeZoneInfo.Utc"/> carries the Windows id <c>UTC</c>, which CLDR
    /// maps to <c>Etc/UTC</c>; both resolve everywhere, and this keeps the readable one in the row.
    /// </summary>
    internal const string UtcId = "UTC";

    /// <summary>
    /// Resolves an id to a zone, accepting either the IANA or the Windows spelling.
    /// </summary>
    /// <exception cref="ArgumentException">The id is empty, or this system cannot resolve it.</exception>
    internal static TimeZoneInfo Resolve(string timeZoneId)
    {
        if (!TryResolve(timeZoneId, out var zone))
        {
            throw new ArgumentException(
                $"'{timeZoneId}' is not a time zone this system can resolve. Use an IANA id such as " +
                "'Europe/Rome', or a Windows id such as 'W. Europe Standard Time'.", nameof(timeZoneId));
        }

        return zone;
    }

    /// <summary>
    /// The non-throwing half of <see cref="Resolve"/>, for the read paths that must report "no zone" rather
    /// than fail a delivery (the execution context) — the schedule itself was validated long before.
    /// </summary>
    internal static bool TryResolve(string? timeZoneId, [NotNullWhen(true)] out TimeZoneInfo? zone)
    {
        zone = null;

        if (string.IsNullOrWhiteSpace(timeZoneId))
            return false;

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>
    /// The id to persist for <paramref name="zone"/>: its IANA spelling, resolved through CLDR when the
    /// platform handed back a Windows id (which is what Windows does even for a zone asked for by IANA id).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The zone has no IANA id — a zone built with <see cref="TimeZoneInfo.CreateCustomTimeZone(string, TimeSpan, string, string)"/>
    /// has rules that live only in this process, so no id could bring it back on the next run.
    /// </exception>
    internal static string Normalize(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        if (string.Equals(zone.Id, UtcId, StringComparison.OrdinalIgnoreCase))
            return UtcId;

        if (zone.HasIanaId)
            return zone.Id;

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var ianaId))
            return ianaId;

        throw new ArgumentException(
            $"The time zone '{zone.Id}' has no IANA id, so it cannot be persisted with the schedule. " +
            "Custom time zones are not supported: use a system zone.", nameof(zone));
    }

    /// <summary>
    /// Resolves <paramref name="timeZoneId"/> and returns the id to persist for it — the two steps a builder
    /// does together, so an id that will not come back on the next run is refused before it is stored (T2).
    /// </summary>
    internal static string Normalize(string timeZoneId) => Normalize(Resolve(timeZoneId));
}
