using System.Text.Json.Serialization;
using Cronos;

namespace EverTask.Scheduler.Recurring.Intervals;

public class CronInterval : IInterval
{
    private CronExpression? _parsedExpression;
    private string _cronExpression = "";

    //used for serialization/deserialization
    [JsonConstructor]
    public CronInterval() { }

    public CronInterval(string cronExpression)
    {
        CronExpression = cronExpression;
    }

    public string CronExpression
    {
        get => _cronExpression;
        set
        {
            if (_cronExpression != value)
            {
                _cronExpression = value;
                _parsedExpression = null; // Invalidate cache
            }
        }
    }

    private CronExpression GetParsedExpression()
    {
        if (_parsedExpression != null)
            return _parsedExpression;

        var fields = CronExpression.Split(' ');

        _parsedExpression = fields.Length switch
        {
            6 => Cronos.CronExpression.Parse(CronExpression, CronFormat.IncludeSeconds),
            5 => Cronos.CronExpression.Parse(CronExpression, CronFormat.Standard),
            _ => throw new ArgumentException("Invalid Cron Expression", nameof(CronExpression))
        };

        return _parsedExpression;
    }

    public CronExpression ParseCronExpression() => GetParsedExpression();

    /// <summary>
    /// Validates that the cron expression parses (correct field count + Cronos-parseable). A persisted cron
    /// string that is corrupt/empty-but-set deserializes fine but throws at the next-run calculation — calling
    /// this right after a recovery deserialize routes the throw to the terminal poison path (B2/gap #1).
    /// </summary>
    public void Validate()
    {
        if (!string.IsNullOrEmpty(CronExpression))
            GetParsedExpression(); // throws ArgumentException on an unparseable cron expression
    }

    /// <summary>
    /// The next occurrence strictly after <paramref name="current"/>, with the expression read as UTC.
    /// </summary>
    /// <remarks>
    /// Kept as its own zero-zone method rather than an optional parameter on the overload below: the original
    /// IL signature is what an assembly compiled against the previous release calls (P6/X6).
    /// </remarks>
    public DateTimeOffset? GetNextOccurrence(DateTimeOffset current) =>
        GetNextOccurrence(current, TimeZoneInfo.Utc);

    /// <summary>
    /// The next occurrence strictly after <paramref name="current"/>, with the expression read on
    /// <paramref name="zone"/>'s clock.
    /// </summary>
    /// <remarks>
    /// Cronos owns the DST rules here: a skipped local time fires at the transition, a repeated one fires on
    /// its first pass, and an interval expression (<c>*/n</c>) keeps stepping through both. That is why T9
    /// makes it the oracle the fluent API's own zone math is measured against, rather than a second
    /// implementation to keep in agreement.
    /// </remarks>
    public DateTimeOffset? GetNextOccurrence(DateTimeOffset current, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        return GetParsedExpression().GetNextOccurrence(current, zone)?.ToUniversalTime();
    }
}
