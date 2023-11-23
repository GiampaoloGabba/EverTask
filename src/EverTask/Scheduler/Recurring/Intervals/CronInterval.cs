using Cronos;

namespace EverTask.Scheduler.Recurring.Intervals;

public class CronInterval : IInterval
{
    //used for serialization/deserialization
    public CronInterval() { }

    public CronInterval(string cronExpression)
    {
        CronExpression = cronExpression;
    }

    public string CronExpression { get; set; } = "";

    public CronExpression ParseCronExpression()
    {
        var fields = CronExpression.Split(' ');

        return fields.Length switch
        {
            6 => Cronos.CronExpression.Parse(CronExpression, CronFormat.IncludeSeconds),
            5 => Cronos.CronExpression.Parse(CronExpression, CronFormat.Standard),
            _ => throw new ArgumentException("Invalid Cron Expression", nameof(CronExpression))
        };
    }

    public DateTimeOffset? GetNextOccurrence(DateTimeOffset current) =>
        ParseCronExpression().GetNextOccurrence(current, TimeZoneInfo.Utc)?.ToUniversalTime();
}
