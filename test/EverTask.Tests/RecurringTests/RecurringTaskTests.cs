using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.RecurringTests;

public class RecurringTaskTests
{
    [Fact]
    public void CalculateNextRun_WithRunNow_ShouldReturnImmediateTime()
    {
        var task    = new RecurringTask { RunNow = true };
        var current = DateTimeOffset.UtcNow;
        var nextRun = task.CalculateNextRun(current, 0);

        Assert.True(nextRun <= DateTimeOffset.UtcNow.AddSeconds(5));
    }

    [Fact]
    public void CalculateNextRun_WithFutureSpecificRunTime_ShouldReturnSpecificTime()
    {
        var futureTime = DateTimeOffset.UtcNow.AddHours(1);
        var task       = new RecurringTask { SpecificRunTime = futureTime };
        var nextRun    = task.CalculateNextRun(DateTimeOffset.UtcNow, 0);

        Assert.Equal(futureTime, nextRun);
    }

    // Testa runtime molto vicino a next
    [Fact]
    public void CalculateNextRun_WithCloseRuntimeAndNext_ShouldReturnNext()
    {
        var task = new RecurringTask
        {
            RunNow         = true,
            SecondInterval = new SecondInterval(30) // Ogni 30 secondi
        };
        var current = DateTimeOffset.UtcNow.AddSeconds(-15); // 15 secondi prima
        var nextRun = task.CalculateNextRun(current, 0);

        // Aspettiamo che il metodo restituisca 'next', non 'runtime',
        // perché 'runtime' è troppo vicino a 'next'
        Assert.True(nextRun >= current.AddSeconds(30));
    }

    // Testa runtime nel passato rispetto a current
    [Fact]
    public void CalculateNextRun_WithPastRuntime_close_to_now_should_run_ShouldReturnRunTime()
    {
        var pastTime = DateTimeOffset.UtcNow.AddSeconds(-15);
        var task     = new RecurringTask { SpecificRunTime = pastTime, HourInterval = new HourInterval(1) };
        var current  = DateTimeOffset.UtcNow;
        var nextRun  = task.CalculateNextRun(current, 0);

        // Aspettiamo che il metodo restituisca 'next', non 'runtime',
        // perché 'runtime' è nel passato rispetto a 'current'
        Assert.True(nextRun == pastTime);
    }

    // Testa runtime nel passato rispetto a current
    [Fact]
    public void CalculateNextRun_WithPastRuntime_should_run_ShouldReturnNext()
    {
        var pastTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        var task     = new RecurringTask { SpecificRunTime = pastTime, MinuteInterval = new MinuteInterval(30) };
        var current  = DateTimeOffset.UtcNow;
        var nextRun  = task.CalculateNextRun(current, 0);

        // Aspettiamo che il metodo restituisca 'next', non 'runtime',
        // perché 'runtime' è nel passato rispetto a 'current'
        Assert.True(nextRun > current);
    }

    // Testa runtime e next con gap sufficiente
    [Fact]
    public void CalculateNextRun_WithSufficientGapBetweenRuntimeAndNext_ShouldReturnRuntime()
    {
        var task = new RecurringTask
        {
            RunNow         = true,
            MinuteInterval = new MinuteInterval(10) // Ogni 10 minuti
        };
        var current = DateTimeOffset.UtcNow.AddMinutes(-10); // 10 minuti prima
        var nextRun = task.CalculateNextRun(current, 0);

        // Aspettiamo che il metodo restituisca 'runtime' perché c'è un gap sufficiente
        Assert.True(nextRun <= DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void CalculateNextRun_WithPastSpecificRunTime_ShouldReturnPasTimeForFirtRun()
    {
        var pastTime = DateTimeOffset.UtcNow.AddHours(-1);
        var task     = new RecurringTask { SpecificRunTime = pastTime };
        var nextRun  = task.CalculateNextRun(DateTimeOffset.UtcNow, 0);

        Assert.NotNull(nextRun);
    }

    [Fact]
    public void CalculateNextRun_WithPastSpecificRunTime_ShouldReturnNullForNotFirstRun()
    {
        var pastTime = DateTimeOffset.UtcNow.AddHours(-1);
        var task     = new RecurringTask { SpecificRunTime = pastTime };
        var nextRun  = task.CalculateNextRun(DateTimeOffset.UtcNow, 1);

        Assert.Null(nextRun);
    }

    [Fact]
    public void CalculateNextRun_WithInitialDelay_ShouldReturnDelayedTime()
    {
        var delay   = TimeSpan.FromMinutes(30);
        var task    = new RecurringTask { InitialDelay = delay };
        var current = DateTimeOffset.UtcNow;
        var nextRun = task.CalculateNextRun(current, 0);

        var expectedTime = current.Add(delay);
        Assert.True(nextRun >= expectedTime && nextRun <= expectedTime.AddSeconds(1));
    }

    [Fact]
    public void CalculateNextRun_WithSecondInterval_ShouldReturnNextSecond()
    {
        var task    = new RecurringTask { SecondInterval = new SecondInterval(10) };
        var current = DateTimeOffset.UtcNow;
        var nextRun = task.CalculateNextRun(current, 0);

        var expectedTime = current.AddSeconds(10);
        Assert.True(nextRun >= expectedTime && nextRun <= expectedTime.AddSeconds(1));
    }

    [Fact]
    public void CalculateNextRun_ShouldReturnNextCronOccurrence()
    {
        var task = new RecurringTask
        {
            CronInterval = new CronInterval("* * * * *") // Every minute
        };

        var current = DateTimeOffset.UtcNow;
        var nextRun = task.CalculateNextRun(current, 0);

        Assert.NotNull(nextRun);
        Assert.True(nextRun > current);
    }

    [Fact]
    public void CalculateNextRun_ShouldReturnNullIfMaxRunsExceeded()
    {
        var task = new RecurringTask
        {
            MaxRuns = 1
        };

        var nextRun = task.CalculateNextRun(DateTimeOffset.UtcNow, 1);

        Assert.Null(nextRun);
    }

    // Testa con MinuteInterval
    [Fact]
    public void CalculateNextRun_WithMinuteInterval_ShouldReturnNextMinute()
    {
        var task    = new RecurringTask { MinuteInterval = new MinuteInterval(10) }; // Ogni 10 minuti
        var current = DateTimeOffset.UtcNow;
        var nextRun = task.CalculateNextRun(current, 0);

        Assert.NotNull(nextRun);
        // Verifica che la prossima esecuzione sia entro 10 minuti
        Assert.True(nextRun >= current && nextRun <= current.AddMinutes(10));
    }

    // Testa con HourInterval
    [Fact]
    public void CalculateNextRun_WithHourInterval_ShouldReturnNextHour()
    {
        var task    = new RecurringTask { HourInterval = new HourInterval(1) }; // Ogni ora
        var current = DateTimeOffset.UtcNow;
        var nextRun = task.CalculateNextRun(current, 0);

        Assert.NotNull(nextRun);
        // Verifica che la prossima esecuzione sia entro 1 ora
        Assert.True(nextRun >= current && nextRun <= current.AddHours(1));
    }

    // Testa con DayInterval
    [Fact]
    public void CalculateNextRun_WithDayInterval_ShouldReturnStartOfNextDay()
    {
        var task    = new RecurringTask { DayInterval = new DayInterval(1) }; // Ogni giorno
        var current = DateTimeOffset.UtcNow;
        var nextRun = task.CalculateNextRun(current, 0);

        Assert.NotNull(nextRun);

        // Calcola l'inizio del giorno successivo (mezzanotte)
        var expectedNextRun =
            new DateTimeOffset(current.Year, current.Month, current.Day, 0, 0, 0, TimeSpan.Zero).AddDays(2);
        Assert.Equal(expectedNextRun, nextRun);
    }

    // Testa con MonthInterval
    [Fact]
    public void CalculateNextRun_WithMonthInterval_ShouldReturnNextMonth()
    {
        var task    = new RecurringTask { MonthInterval = new MonthInterval(1) }; // Ogni mese
        var current = DateTimeOffset.UtcNow;
        var nextRun = task.CalculateNextRun(current, 0);

        Assert.NotNull(nextRun);
        // Verifica che la prossima esecuzione sia il mese successivo
        Assert.True(nextRun >= current && nextRun <= current.AddMonths(1));
    }
}
