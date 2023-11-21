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

    /*IEverySchedulerBuilder Every(int number);

    IMinuteSchedulerBuilder EveryMinute();
    IDailyTimeSchedulerBuilder EveryDay();
    IMonthlySchedulerBuilder EveryMonth();
    IDailyTimeSchedulerBuilder OnDays(params Day[] days);

    IMonthlySchedulerBuilder OnMonths(params Month[] months);*/
}

public interface IEverySchedulerBuilder
{
    IMinuteSchedulerBuilder Minutes();
    IBuildableSchedulerBuilder Hours();
    IDailyTimeSchedulerBuilder Days();
    IMonthlySchedulerBuilder Months();
}

public interface IMinuteSchedulerBuilder
{
    IBuildableSchedulerBuilder AtSecond(int second);
}

public interface IDailyTimeSchedulerBuilder : IBuildableSchedulerBuilder
{
    IBuildableSchedulerBuilder AtTime(TimeOnly time);
    IBuildableSchedulerBuilder AtTime(params TimeOnly[] times);
}

public interface IMonthlySchedulerBuilder : IBuildableSchedulerBuilder
{
    IDailyTimeSchedulerBuilder OnDay(int day);
    IDailyTimeSchedulerBuilder OnFirst(Day day);
}

public interface IThenableSchedulerBuilder
{
    IIntervalSchedulerBuilder Then();
}

public interface IBuildableSchedulerBuilder
{
    void MaxRuns(int maxRuns);
}
