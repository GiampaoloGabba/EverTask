using Cronos;
using Newtonsoft.Json;

namespace EverTask.Scheduler.Recurring.Intervals;

public class CronInterval : IInterval
{
    private Cronos.CronExpression? _parsedExpression;
    private string _cronExpression = "";

    //used for serialization/deserialization
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

    private Cronos.CronExpression GetParsedExpression()
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

    public DateTimeOffset? GetNextOccurrence(DateTimeOffset current) =>
        GetParsedExpression().GetNextOccurrence(current, TimeZoneInfo.Utc)?.ToUniversalTime();
}
