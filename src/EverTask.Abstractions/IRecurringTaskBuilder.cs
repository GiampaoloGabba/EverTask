namespace EverTask.Abstractions;

public interface IRecurringTaskBuilder
{
    IThenableSchedulerBuilder RunNow();
    IThenableSchedulerBuilder RunDelayed(TimeSpan delay);
    IThenableSchedulerBuilder RunAt(DateTimeOffset dateTimeOffset);

    IIntervalSchedulerBuilder Schedule();
}

public interface IIntervalSchedulerBuilder
{
    IBuildableSchedulerBuilder UseCron(string cronExpression);

    IEverySchedulerBuilder Every(int number);

    IBuildableSchedulerBuilder EverySecond();
    IMinuteSchedulerBuilder EveryMinute();
    IHourSchedulerBuilder EveryHour();
    IDailyTimeSchedulerBuilder EveryDay();
    IWeeklySchedulerBuilder EveryWeek();
    IMonthlySchedulerBuilder EveryMonth();

    IDailyTimeSchedulerBuilder OnDays(params DayOfWeek[] days);
    IMonthlySchedulerBuilder OnMonths(params int[] months);
}

public interface IEverySchedulerBuilder
{
    IBuildableSchedulerBuilder Seconds();
    IMinuteSchedulerBuilder Minutes();
    IHourSchedulerBuilder Hours();
    IDailyTimeSchedulerBuilder Days();
    IWeeklySchedulerBuilder Weeks();
    IMonthlySchedulerBuilder Months();
}

public interface IHourSchedulerBuilder
{
    IMinuteSchedulerBuilder AtMinute(int minute);
    IBuildableSchedulerBuilder RunUntil(DateTimeOffset dateTimeOffset);
    void MaxRuns(int maxRuns);
}

public interface IMinuteSchedulerBuilder
{
    IBuildableSchedulerBuilder AtSecond(int second);
    IBuildableSchedulerBuilder RunUntil(DateTimeOffset dateTimeOffset);
    void MaxRuns(int maxRuns);
}

public interface IDailyTimeSchedulerBuilder : IBuildableSchedulerBuilder
{
    IBuildableSchedulerBuilder AtTime(TimeOnly time);
    IBuildableSchedulerBuilder AtTimes(params TimeOnly[] times);
}

/// <summary>
/// Builder for configuring weekly recurring tasks
/// </summary>
public interface IWeeklySchedulerBuilder : IBuildableSchedulerBuilder
{
    /// <summary>
    /// Specifies the task should run on a specific day of the week
    /// </summary>
    IDailyTimeSchedulerBuilder OnDay(DayOfWeek day);

    /// <summary>
    /// Specifies the task should run on specific days of the week
    /// </summary>
    IDailyTimeSchedulerBuilder OnDays(params DayOfWeek[] days);
}

public interface IMonthlySchedulerBuilder : IBuildableSchedulerBuilder
{
    IDailyTimeSchedulerBuilder OnDay(int day);
    IDailyTimeSchedulerBuilder OnDays(params int[] day);
    IDailyTimeSchedulerBuilder OnFirst(DayOfWeek day);
}

public interface IThenableSchedulerBuilder
{
    IIntervalSchedulerBuilder Then();
}

public interface IBuildableSchedulerBuilder
{
    IBuildableSchedulerBuilder RunUntil(DateTimeOffset dateTimeOffset);
    void MaxRuns(int maxRuns);
}
