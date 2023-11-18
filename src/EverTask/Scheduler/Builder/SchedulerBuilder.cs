/*
namespace EverTask.Scheduler.Builder;

public class SchedulerBuilder : ISchedulerBuilder, IDailyTimeSchedulerBuilder, IMonthlySchedulerBuilder,
    IYearlySchedulerBuilder, IThenableSchedulerBuilder
{
    private readonly ScheduledTask _scheduledTask = new();
    private SchedulingRule _currentRule = new();
    private bool _runNowCalled;

    public ISchedulerBuilder SetTimeZoneOffset(TimeSpan offset)
    {
        _scheduledTask.TimeZoneOffset = offset;
        return this;
    }

    public IThenableSchedulerBuilder RunNow()
    {
        if (_runNowCalled)
        {
            throw new InvalidOperationException("RunNow can only be called once.");
        }

        _scheduledTask.RunImmediately = true;
        _runNowCalled                 = true;
        return this;
    }

    public IThenableSchedulerBuilder RunDelayed(TimeSpan delay)
    {
        _currentRule.InitialDelay = delay;
        return this;
    }

    public IThenableSchedulerBuilder RunAt(DateTimeOffset dateTimeOffset)
    {
        _currentRule.SpecificRunTimes.Add(dateTimeOffset);
        return this;
    }

    public IDailyTimeSchedulerBuilder EveryDay(int interval = 1)
    {
        _currentRule.DayInterval = interval;
        return this;
    }

    ScheduledTask IYearlySchedulerBuilder.Build()
    {
        return Build();
    }

    ScheduledTask IMonthlySchedulerBuilder.Build()
    {
        return Build();
    }

    public IMonthlySchedulerBuilder EveryMonth(int interval = 1)
    {
        _currentRule.MonthInterval = interval;
        return this;
    }

    public IYearlySchedulerBuilder EveryYear()
    {
        _currentRule.YearInterval = 1;
        return this;
    }

    public IDailyTimeSchedulerBuilder AtTime(TimeOnly time)
    {
        _currentRule.DailyTimes.Add(time);
        return this;
    }

    public IDailyTimeSchedulerBuilder AtTime(params TimeOnly[] times)
    {
        _currentRule.DailyTimes.AddRange(times);
        return this;
    }

    public IDailyTimeSchedulerBuilder OnDays(params Day[] days)
    {
        _currentRule.DaysOfWeek.AddRange(days);
        return this;
    }

    ScheduledTask IDailyTimeSchedulerBuilder.Build()
    {
        return Build();
    }

    public IMonthlySchedulerBuilder InMonths(params Month[] months)
    {
        _currentRule.MonthsOfYear.AddRange(months);
        return this;
    }

    public IMonthlySchedulerBuilder OnFirst(Day day)
    {
        // Assumendo che possiamo memorizzare solo un giorno "OnFirst"
        // Se necessario, modifica per supportare più giorni
        _currentRule.DaysOfWeek.Clear();
        _currentRule.DaysOfWeek.Add(day);
        return this;
    }

    public ISchedulerBuilder Then()
    {
        if (!IsRuleEmpty(_currentRule))
        {
            _scheduledTask.SchedulingRules.Add(_currentRule);
        }

        _currentRule = new SchedulingRule();
        return this;
    }

    public ScheduledTask Build()
    {
        // Aggiungi l'ultima regola se non è vuota
        if (!IsRuleEmpty(_currentRule))
        {
            _scheduledTask.SchedulingRules.Add(_currentRule);
        }

        return _scheduledTask;
    }

    private bool IsRuleEmpty(SchedulingRule rule) =>
        _scheduledTask.RunImmediately == false && rule is
        {
            InitialDelay: null,
            SpecificRunTimes.Count: 0,
            DailyTimes.Count: 0,
            DaysOfWeek.Count: 0,
            DaysOfMonth.Count: 0,
            MonthsOfYear.Count: 0,
            DayInterval: null,
            MonthInterval: null,
            YearInterval: null
        };
}
*/
