﻿namespace EverTask.Abstractions;

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
    IMonthlySchedulerBuilder EveryMonth();

    IDailyTimeSchedulerBuilder OnDays(params DayOfWeek[] days);
    IMonthlySchedulerBuilder OnMonths(params int[] months);
}

public interface IEverySchedulerBuilder
{
    IBuildableSchedulerBuilder Seconds();
    IMinuteSchedulerBuilder Minutes();
    IBuildableSchedulerBuilder Hours();
    IDailyTimeSchedulerBuilder Days();
    IMonthlySchedulerBuilder Months();
}

public interface IHourSchedulerBuilder
{
    IMinuteSchedulerBuilder AtMinute(int minute);
    void MaxRuns(int maxRuns);
}

public interface IMinuteSchedulerBuilder
{
    IBuildableSchedulerBuilder AtSecond(int second);
    void MaxRuns(int maxRuns);
}

public interface IDailyTimeSchedulerBuilder : IBuildableSchedulerBuilder
{
    IBuildableSchedulerBuilder AtTime(TimeOnly time);
    IBuildableSchedulerBuilder AtTime(params TimeOnly[] times);
}

public interface IMonthlySchedulerBuilder : IBuildableSchedulerBuilder
{
    IDailyTimeSchedulerBuilder OnDay(int day);
    IDailyTimeSchedulerBuilder OnFirst(DayOfWeek day);
}

public interface IThenableSchedulerBuilder
{
    IIntervalSchedulerBuilder Then();
}

public interface IBuildableSchedulerBuilder
{
    void MaxRuns(int maxRuns);
}
