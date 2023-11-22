using EverTask.Scheduler.Recurring;

namespace EverTask.Tests.RecurringTests;

public class DateTimeOffsetExtensionsTests
{
    [Theory]
    [InlineData(2023, 2, 30, 28)] // Febbraio 2023, 30 diventa 28
    [InlineData(2023, 4, 31, 30)] // Aprile 2023, 31 diventa 30
    [InlineData(2024, 2, 30, 29)] // Febbraio 2024 (anno bisestile), 30 diventa 29
    public void AdjustDayToValidMonthDay_ValidatesDay(int year, int month, int invalidDay, int expectedDay)
    {
        var dateTime     = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
        var adjustedDate = dateTime.AdjustDayToValidMonthDay(invalidDay);

        Assert.Equal(expectedDay, adjustedDate.Day);
    }

    [Theory]
    [InlineData(2023, 11, 22, 12, 30, new[] { "13:00", "15:00" }, "13:00")]       // Stesso giorno, orario successivo
    [InlineData(2023, 11, 22, 16, 30, new[] { "13:00", "15:00" }, "13:00", true)] // Giorno successivo, primo orario
    public void GetNextRequestedTime_ReturnsCorrectTime(int year, int month, int day, int hour, int minute,
                                                        string[] onTimes, string expectedTime, bool addDays = false)
    {
        var current          = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);
        var onTimesParsed    = onTimes.Select(TimeOnly.Parse).ToArray();
        var expectedTimeOnly = TimeOnly.Parse(expectedTime);
        var expectedDateTime =
            new DateTimeOffset(current.Date.AddHours(expectedTimeOnly.Hour).AddMinutes(expectedTimeOnly.Minute),
                TimeSpan.Zero);

        if (addDays)
        {
            expectedDateTime = expectedDateTime.AddDays(1);
        }

        var nextTime = current.GetNextRequestedTime(current, onTimesParsed);

        nextTime.ShouldBe(expectedDateTime);
    }

    [Theory]
    [InlineData(2023, 11, 22, new[] { DayOfWeek.Saturday }, DayOfWeek.Saturday)] // Da martedì a sabato
    [InlineData(2023, 11, 22, new[] { DayOfWeek.Monday }, DayOfWeek.Monday)]     // Da martedì al prossimo lunedì
    public void NextValidDayOfWeek_FindsCorrectDay(int year, int month, int day, DayOfWeek[] validDays,
                                                   DayOfWeek expectedDayOfWeek)
    {
        var dateTime     = new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero);
        var nextValidDay = dateTime.NextValidDayOfWeek(validDays);

        Assert.Equal(expectedDayOfWeek, nextValidDay.DayOfWeek);
    }

    [Theory]
    [InlineData(2023, 11, 22, new[] { 25 }, 25)] // Cerca il prossimo giorno 25 dello stesso mese
    [InlineData(2023, 11, 30, new[] { 5 }, 5)]   // Cerca il giorno 5 del mese successivo
    public void NextValidDay_FindsCorrectDay(int year, int month, int day, int[] validDays, int expectedDay)
    {
        var dateTime     = new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero);
        var nextValidDay = dateTime.NextValidDay(validDays);

        Assert.Equal(expectedDay, nextValidDay.Day);
    }


    [Theory]
    [InlineData(2023, 11, 22, 10, new[] { 12, 15, 18 }, 12)] // Da 10:00 a 12:00
    [InlineData(2023, 11, 22, 16, new[] { 12, 15, 18 }, 18)] // Da 16:00 a 18:00
    [InlineData(2023, 11, 22, 19, new[] { 12, 15, 18 }, 12)] // Da 19:00 al giorno successivo alle 12:00
    public void NextValidHour_ReturnsCorrectHour(int year, int month, int day, int hour, int[] validHours,
                                                 int expectedHour)
    {
        var dateTime      = new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero);
        var nextValidHour = dateTime.NextValidHour(validHours);

        Assert.Equal(expectedHour, nextValidHour.Hour);

        if (hour > expectedHour)
        {
            Assert.Equal(day + 1, nextValidHour.Day);
        }
        else
        {
            Assert.Equal(day, nextValidHour.Day);
        }
    }

    [Theory]
    [InlineData(2023, 11, new[] { 12 }, 12)] // Trova il prossimo dicembre
    [InlineData(2023, 12, new[] { 3 }, 3)]   // Dal dicembre 2023 al marzo 2024
    public void NextValidMonth_FindsCorrectMonth(int year, int startMonth, int[] validMonths, int expectedMonth)
    {
        var dateTime       = new DateTimeOffset(year, startMonth, 1, 0, 0, 0, TimeSpan.Zero);
        var nextValidMonth = dateTime.NextValidMonth(validMonths);

        Assert.Equal(expectedMonth, nextValidMonth.Month);
    }

    [Theory]
    [InlineData(2023, 11, DayOfWeek.Friday, 3)] // Trova il primo venerdì di novembre 2023
    [InlineData(2023, 12, DayOfWeek.Monday, 4)] // Trova il primo lunedì di dicembre 2023
    public void FindFirstOccurrenceOfDayOfWeekInMonth_FindsCorrectDay(int year, int month, DayOfWeek dayOfWeek,
                                                                      int expectedDay)
    {
        var dateTime        = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
        var firstOccurrence = dateTime.FindFirstOccurrenceOfDayOfWeekInMonth(dayOfWeek);

        Assert.Equal(expectedDay, firstOccurrence.Day);
    }

    [Theory]
    [InlineData(15, 30, -2, 17, 30)] // Zona oraria a -2 ore da UTC (15:30 locale equivale a 17:30 UTC)
    [InlineData(15, 30, 3, 12, 30)]  // Zona oraria a +3 ore da UTC (15:30 locale equivale a 12:30 UTC)
    public void ToUniversalTime_ConvertsCorrectly(int localHour, int localMinute, int offsetHours, int expectedUtcHour,
                                                  int expectedUtcMinute)
    {
        var localDateTime = new DateTimeOffset(2023, 1, 1, localHour, localMinute, 0, TimeSpan.FromHours(offsetHours));
        var utcDateTime   = localDateTime.ToUniversalTime();

        var expectedUtcTime = new TimeOnly(expectedUtcHour, expectedUtcMinute);
        var actualUtcTime   = new TimeOnly(utcDateTime.Hour, utcDateTime.Minute);

        Assert.Equal(expectedUtcTime, actualUtcTime);
    }


    [Fact]
    public void Adjust_AdjustsDateTimeCorrectly()
    {
        var dateTime         = new DateTimeOffset(2023, 11, 22, 12, 30, 0, TimeSpan.Zero);
        var adjustedDateTime = dateTime.Adjust(day: 25, hour: 14, minute: 45);

        Assert.Equal(25, adjustedDateTime.Day);
        Assert.Equal(14, adjustedDateTime.Hour);
        Assert.Equal(45, adjustedDateTime.Minute);
    }
}
