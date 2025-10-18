# Recurring Task Scheduler

## Purpose

This subsystem provides fluent API builders and interval calculation logic for recurring task execution. It supports multiple interval types (second, minute, hour, day, month) plus cron expressions, with optional initial delays, specific run times, maximum runs, and end dates.

## Architecture

### Component Relationships

```
Dispatcher.Dispatch(task, recurring => ...)
    -> RecurringTaskBuilder (entry point)
        -> Interval Builders (EverySchedulerBuilder, DailyTimeSchedulerBuilder, etc.)
            -> Interval Types (SecondInterval, MinuteInterval, HourInterval, DayInterval, MonthInterval, CronInterval)
                -> RecurringTask (configuration object)
                    -> TimerScheduler (calculates next run)
```

### Builder Pattern Flow

1. **RecurringTaskBuilder** (`Builder/RecurringTaskBuilder.cs:3-26`) - Entry point with initial run options
2. **ThenableSchedulerBuilder** (`Builder/RecurringTaskBuilder.cs:28-31`) - Bridges initial run to interval configuration
3. **IntervalSchedulerBuilder** (`Builder/IntervalSchedulerBuilder.cs`) - Provides `Every()` and `Cron()` methods
4. **EverySchedulerBuilder** (`Builder/EverySchedulerBuilder.cs:3-46`) - Interval type selection (seconds/minutes/hours/days/months)
5. **Specific Builders** (`MinuteSchedulerBuilder`, `HourSchedulerBuilder`, `DailyTimeSchedulerBuilder`, `MonthlySchedulerBuilder`) - Fine-grained scheduling
6. **BuildableSchedulerBuilder** (`Builder/RecurringTaskBuilder.cs:33-46`) - Terminal builder with `RunUntil()` and `MaxRuns()`

## Key Components

### RecurringTask (`RecurringTask.cs`)

**Purpose**: Immutable configuration object containing all scheduling parameters.

**Critical Properties**:
- `5`: `RunNow` - execute immediately, then follow interval
- `6`: `InitialDelay` - delay before first execution
- `7`: `SpecificRunTime` - exact first run time
- `8-13`: Interval types - only one should be set (Cron overrides others)
- `14`: `MaxRuns` - stop after N executions (null = infinite)
- `15`: `RunUntil` - stop after date

**Critical Methods**:
- `21-58`: `CalculateNextRun(current, currentRun)` - main scheduling logic
  - `23`: Returns null if `MaxRuns` reached (stops recurring task)
  - `27`: Returns null if `RunUntil` exceeded (stops recurring task)
  - `29`: Delegates to `GetNextOccurrence()` for interval calculation
  - `33`: Returns immediately if not first run (`currentRun > 0`)
  - `35-46`: First run logic - prioritizes `RunNow`, `SpecificRunTime`, `InitialDelay`
  - `50-55`: **30-Second Gap Rule** - prevents overlapping executions when `runtime` is before `next` interval
- `60-81`: `GetNextOccurrence(current)` - calculates next interval-based run
  - `62-68`: Cron expression takes precedence (bypasses other intervals)
  - `71-75`: Interval cascade - month -> day -> hour -> minute -> second (each refines previous)
  - `77-78`: Validates next run is future and before `RunUntil`

**Serialization**: All properties have parameterless constructors for JSON deserialization (lines 7-8 in intervals).

### RecurringTaskBuilder (`Builder/RecurringTaskBuilder.cs`)

**Purpose**: Entry point for fluent API, handles initial run configuration.

**Public Methods**:
- `7-11`: `RunNow()` - sets `RunNow = true`, returns `IThenableSchedulerBuilder`
- `13-17`: `RunDelayed(delay)` - sets `InitialDelay`, returns `IThenableSchedulerBuilder`
- `19-23`: `RunAt(dateTimeOffset)` - sets `SpecificRunTime`, returns `IThenableSchedulerBuilder`
- `25`: `Schedule()` - skips initial run config, returns `IIntervalSchedulerBuilder`

**Builder Transition**: Returns `ThenableSchedulerBuilder` to bridge to interval selection.

### ThenableSchedulerBuilder (`Builder/RecurringTaskBuilder.cs:28-31`)

**Purpose**: Transition builder between initial run and interval configuration.

**Method**:
- `30`: `Then()` - returns `IntervalSchedulerBuilder` for interval configuration

**Usage Example**:
```csharp
recurring.RunDelayed(TimeSpan.FromHours(1))
    .Then()
    .Every(5).Minutes()
```

### IntervalSchedulerBuilder (`Builder/IntervalSchedulerBuilder.cs`)

**Purpose**: Provides `Every(n)` and `Cron(expression)` methods.

**Critical Methods**:
- `Every(int interval)` - returns `EverySchedulerBuilder` for interval type selection
- `Cron(string cronExpression)` - sets `CronInterval`, returns `IBuildableSchedulerBuilder`

**Cron Priority**: Cron expressions bypass all other intervals (`RecurringTask.cs:62-68`).

### EverySchedulerBuilder (`Builder/EverySchedulerBuilder.cs`)

**Purpose**: Interval type selection (seconds, minutes, hours, days, months).

**Constructor**:
- `8-15`: Validates `interval > 0`, stores `_task` and `_interval`

**Methods**:
- `17-21`: `Seconds()` - creates `SecondInterval`, returns `BuildableSchedulerBuilder`
- `23-27`: `Minutes()` - creates `MinuteInterval`, returns `MinuteSchedulerBuilder`
- `29-33`: `Hours()` - creates `HourInterval`, returns `BuildableSchedulerBuilder`
- `35-39`: `Days()` - creates `DayInterval`, returns `DailyTimeSchedulerBuilder`
- `41-45`: `Months()` - creates `MonthInterval`, returns `MonthlySchedulerBuilder`

**Builder Types**: Returns specific builders for fine-grained options (e.g., `Minutes()` returns `MinuteSchedulerBuilder` for `OnSecond()` option).

### MinuteSchedulerBuilder (`Builder/MinuteSchedulerBuilder.cs`)

**Purpose**: Configure second within minute for minute-based intervals.

**Method**:
- `OnSecond(int second)` - sets `MinuteInterval.OnSecond`, returns `BuildableSchedulerBuilder`
- Validates second is 0-59

**Example**:
```csharp
recurring.Every(15).Minutes().OnSecond(30) // Every 15 minutes at :30 seconds
```

### HourSchedulerBuilder (`Builder/HourSchedulerBuilder.cs`)

**Purpose**: Configure minute/second within hour for hour-based intervals.

**Methods**:
- `OnMinute(int minute)` - sets `HourInterval.OnMinute`
- `OnSecond(int second)` - sets `HourInterval.OnSecond` (chainable with `OnMinute()`)
- Validates minute is 0-59, second is 0-59

**Example**:
```csharp
recurring.Every(2).Hours().OnMinute(15).OnSecond(0) // Every 2 hours at :15:00
```

### DailyTimeSchedulerBuilder (`Builder/DailyTimeSchedulerBuilder.cs`)

**Purpose**: Configure time(s) of day and day(s) of week for day-based intervals.

**Methods**:
- `At(int hour, int minute)` - sets `DayInterval.OnTimes` to single time
- `At(params TimeOnly[] times)` - sets multiple times per day
- `On(params DayOfWeek[] days)` - sets specific days of week
- Validates hour is 0-23, minute is 0-59

**Example**:
```csharp
recurring.Every(1).Days().At(9, 30).On(DayOfWeek.Monday, DayOfWeek.Friday) // Weekdays at 9:30 AM
```

### MonthlySchedulerBuilder (`Builder/MonthlySchedulerBuilder.cs`)

**Purpose**: Configure day(s), time(s), and month(s) for month-based intervals.

**Methods**:
- `OnDay(int day)` - specific day of month (1-31)
- `OnFirst(DayOfWeek day)` - first occurrence of weekday in month
- `OnDays(params DayOfWeek[] days)` - specific days of week in month
- `At(int hour, int minute)` / `At(params TimeOnly[] times)` - times of day
- `InMonths(params Month[] months)` - specific months

**Example**:
```csharp
recurring.Every(1).Months().OnFirst(DayOfWeek.Monday).At(9, 0) // First Monday at 9:00 AM monthly
```

### BuildableSchedulerBuilder (`Builder/RecurringTaskBuilder.cs:33-46`)

**Purpose**: Terminal builder providing `RunUntil()` and `MaxRuns()` constraints.

**Methods**:
- `35-43`: `RunUntil(dateTimeOffset)` - sets `RecurringTask.RunUntil`
  - `38-39`: Validates not in past
  - `37`: Converts to UTC
- `45`: `MaxRuns(int maxRuns)` - sets `RecurringTask.MaxRuns`

**Usage**: All interval builders eventually return this for finalization.

## Interval Types

### IInterval (`Intervals/IInterval.cs`)

**Purpose**: Common interface for all interval types.

**Method**: `DateTimeOffset? GetNextOccurrence(DateTimeOffset current)` - calculates next execution time.

### SecondInterval (`Intervals/SecondInterval.cs`)

**Purpose**: Simple interval-based scheduling (every N seconds).

**Properties**:
- `Interval` - seconds between executions

**Calculation** (`GetNextOccurrence`):
- Returns `current.AddSeconds(Interval)`

**Example**: `Every(30).Seconds()` - every 30 seconds

### MinuteInterval (`Intervals/MinuteInterval.cs`)

**Purpose**: Minute-based scheduling with optional second specification.

**Properties**:
- `Interval` - minutes between executions
- `OnSecond` - specific second within minute (0-59)

**Calculation** (`GetNextOccurrence`):
- Adds `Interval` minutes to current time
- If `OnSecond` set, adjusts to specific second via `DateTimeExtensions.Adjust()`

**Example**: `Every(15).Minutes().OnSecond(30)` - every 15 minutes at :30 seconds

### HourInterval (`Intervals/HourInterval.cs`)

**Purpose**: Hour-based scheduling with optional minute/second and specific hours.

**Properties**:
- `Interval` - hours between executions
- `OnHours` - specific hours of day (0-23 array)
- `OnMinute` - specific minute within hour (0-59)
- `OnSecond` - specific second within minute (0-59)

**Calculation** (`GetNextOccurrence`):
- Adds `Interval` hours to current time
- If `OnHours` set, advances to next valid hour via `DateTimeExtensions.NextValidHour()`
- If `OnMinute` set, adjusts minute via `DateTimeExtensions.Adjust()`
- If `OnSecond` set, adjusts second via `DateTimeExtensions.Adjust()`

**Example**: `Every(2).Hours().OnMinute(15)` - every 2 hours at :15:00

### DayInterval (`Intervals/DayInterval.cs`)

**Purpose**: Day-based scheduling with specific times and days of week.

**Properties**:
- `Interval` - days between executions (default 1)
- `OnTimes` - times of day (default "00:00")
- `OnDays` - specific days of week (empty = all days)

**Validation** (`Validate`):
- `25-27`: Throws if `Interval == 0` and no `OnDays` specified

**Calculation** (`GetNextOccurrence`):
- `34`: Adds `Interval` days to current time
- `36-37`: If `OnDays` set, advances to next valid day of week via `DateTimeExtensions.NextValidDayOfWeek()`
- `39`: Adjusts to next requested time via `DateTimeExtensions.GetNextRequestedTime()`

**Examples**:
- `Every(1).Days().At(9, 30)` - daily at 9:30 AM
- `Every(1).Days().On(DayOfWeek.Monday, DayOfWeek.Friday).At(8, 0)` - weekdays at 8:00 AM

### MonthInterval (`Intervals/MonthInterval.cs`)

**Purpose**: Month-based scheduling with day, time, and month specifications.

**Properties**:
- `Interval` - months between executions
- `OnDay` - specific day of month (1-31)
- `OnFirst` - first occurrence of weekday (e.g., first Monday)
- `OnDays` - specific days of week
- `OnTimes` - times of day
- `OnMonths` - specific months (enum: January-December)

**Calculation** (`GetNextOccurrence`):
- Complex logic handling day-of-month, first-weekday, and day-of-week constraints
- See `DateTimeExtensions` for helper methods

**Examples**:
- `Every(1).Months().OnDay(15).At(12, 0)` - 15th of each month at noon
- `Every(1).Months().OnFirst(DayOfWeek.Monday).At(9, 0)` - first Monday of each month at 9:00 AM

### CronInterval (`Intervals/CronInterval.cs`)

**Purpose**: Cron expression-based scheduling using Cronos library.

**Property**:
- `CronExpression` - cron expression string

**Methods**:
- `17-27`: `ParseCronExpression()` - parses cron string
  - `23`: 6-field format (includes seconds) - `CronFormat.IncludeSeconds`
  - `24`: 5-field format (standard) - `CronFormat.Standard`
  - `25`: Throws `ArgumentException` for invalid format
- `29-30`: `GetNextOccurrence()` - delegates to `Cronos.CronExpression.GetNextOccurrence()`

**Usage**: `recurring.Cron("0 0 * * MON")` - every Monday at midnight

**Priority**: Cron expressions override all other intervals (`RecurringTask.cs:62-68`).

## DateTimeExtensions (`DateTimeExtensions.cs`)

**Purpose**: Helper methods for interval calculation and date manipulation.

**Key Methods** (inferred from interval usage):
- `Adjust(minute, second)` - sets minute/second within hour
- `NextValidHour(hours)` - advances to next valid hour from array
- `NextValidDayOfWeek(days)` - advances to next valid day of week from array
- `GetNextRequestedTime(current, times)` - finds next occurrence of requested times

**Usage**: Used internally by interval types to calculate next occurrence.

## Execution Flow

### 1. Builder API Usage

```csharp
await dispatcher.Dispatch(new MyTask(), recurring => recurring
    .RunDelayed(TimeSpan.FromHours(1))  // Initial delay
    .Then()
    .Every(15).Minutes()                 // Interval
    .OnSecond(30)                        // Fine-tuning
    .RunUntil(DateTimeOffset.UtcNow.AddMonths(6)) // Constraint
    .MaxRuns(100),                       // Constraint
    ct);
```

**Builder Transitions**:
1. `recurring` parameter is `IRecurringTaskBuilder` (interface for `RecurringTaskBuilder`)
2. `.RunDelayed()` returns `IThenableSchedulerBuilder`
3. `.Then()` returns `IIntervalSchedulerBuilder`
4. `.Every(15)` returns `IEverySchedulerBuilder`
5. `.Minutes()` returns `IMinuteSchedulerBuilder`
6. `.OnSecond(30)` returns `IBuildableSchedulerBuilder`
7. `.RunUntil()` returns `IBuildableSchedulerBuilder`
8. `.MaxRuns()` returns `void` (terminal operation)

### 2. Dispatcher Processing (`Dispatcher.cs:33-38`)

```csharp
var builder = new RecurringTaskBuilder();
recurring(builder); // User builds RecurringTask via fluent API
return await ExecuteDispatch(task, null, builder.RecurringTask, null, cancellationToken);
```

**Builder Access**: `RecurringTaskBuilder.RecurringTask` property (line 5) contains built configuration.

### 3. Task Persistence (`Dispatcher.cs:78-86`)

```csharp
if (recurring != null)
{
    nextRun = recurring.CalculateNextRun(DateTimeOffset.UtcNow, currentRun ?? 0);
    if (nextRun == null)
        throw new ArgumentException("Invalid scheduler recurring expression");
    executionTime = nextRun;
}
```

**First Run Calculation**: `currentRun = 0` triggers initial run logic (`RecurringTask.cs:33-56`).

### 4. Scheduler Enqueue (`Dispatcher.cs:116-119`)

```csharp
if (executor.ExecutionTime > DateTimeOffset.UtcNow || recurring != null)
{
    scheduler.Schedule(executor, nextRun);
}
```

**Scheduler Assignment**: `TimerScheduler` manages recurring tasks via `ConcurrentPriorityQueue`.

### 5. Task Execution (`WorkerExecutor.cs:239-253`)

After task completes:
```csharp
if (task.RecurringTask == null) return;

var currentRun = await taskStorage.GetCurrentRunCount(task.PersistenceId);
var nextRun = task.RecurringTask.CalculateNextRun(DateTimeOffset.UtcNow, currentRun + 1);
await taskStorage.UpdateCurrentRun(task.PersistenceId, nextRun);

if (nextRun.HasValue)
    scheduler.Schedule(task, nextRun);
```

**Rescheduling**:
- Increments `currentRun` (line 245)
- Calculates next run (line 246)
- Updates storage (line 247)
- Re-enqueues to scheduler if next run exists (line 250-251)
- If `nextRun` is null (max runs reached or RunUntil exceeded), task stops

### 6. Next Run Calculation (`RecurringTask.cs:21-58`)

**First Run** (`currentRun == 0`):
1. Check `RunNow` (returns `DateTimeOffset.UtcNow`)
2. Check `SpecificRunTime` (returns specific time)
3. Check `InitialDelay` (returns `current + delay`)
4. If none set, uses interval-based `GetNextOccurrence()`
5. Applies 30-second gap rule (line 50-55)

**Subsequent Runs** (`currentRun > 0`):
- Returns `GetNextOccurrence(current)` directly (line 33)

### 7. Interval Calculation (`RecurringTask.cs:60-81`)

**Cron Priority** (lines 62-68):
- If `CronInterval.CronExpression` set, uses Cronos library exclusively
- Validates `nextCron <= RunUntil`

**Interval Cascade** (lines 71-75):
- Starts with current time
- Applies month interval (if set)
- Refines with day interval (if set)
- Refines with hour interval (if set)
- Refines with minute interval (if set)
- Refines with second interval (if set)
- Each interval builds on previous result

**Validation** (lines 77-78):
- Ensures next run is at least 1 second in future
- Ensures next run is before `RunUntil`

## Common Tasks

### Create Simple Recurring Task

```csharp
// Every 30 seconds
await dispatcher.Dispatch(new MyTask(), r => r.Every(30).Seconds(), ct);

// Every 5 minutes
await dispatcher.Dispatch(new MyTask(), r => r.Every(5).Minutes(), ct);

// Every hour on the hour
await dispatcher.Dispatch(new MyTask(), r => r.Every(1).Hours().OnMinute(0), ct);
```

### Create Daily Task at Specific Time

```csharp
// Daily at 9:00 AM
await dispatcher.Dispatch(new DailyTask(), r => r.Every(1).Days().At(9, 0), ct);

// Multiple times per day
await dispatcher.Dispatch(new DailyTask(), r => r
    .Every(1).Days()
    .At(TimeOnly.Parse("09:00"), TimeOnly.Parse("17:00")), ct);
```

### Create Weekday Task

```csharp
// Monday-Friday at 8:00 AM
await dispatcher.Dispatch(new WeekdayTask(), r => r
    .Every(1).Days()
    .On(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
        DayOfWeek.Thursday, DayOfWeek.Friday)
    .At(8, 0), ct);
```

### Create Monthly Task

```csharp
// 15th of every month at noon
await dispatcher.Dispatch(new MonthlyTask(), r => r
    .Every(1).Months()
    .OnDay(15)
    .At(12, 0), ct);

// First Monday of every month at 9:00 AM
await dispatcher.Dispatch(new MonthlyTask(), r => r
    .Every(1).Months()
    .OnFirst(DayOfWeek.Monday)
    .At(9, 0), ct);
```

### Create Cron Task

```csharp
// Standard cron (5 fields)
await dispatcher.Dispatch(new CronTask(), r => r.Cron("0 0 * * *"), ct); // Midnight daily

// Cron with seconds (6 fields)
await dispatcher.Dispatch(new CronTask(), r => r.Cron("30 0 0 * * *"), ct); // Midnight + 30s daily
```

### Add Initial Delay

```csharp
// Start after 1 hour, then every 15 minutes
await dispatcher.Dispatch(new DelayedTask(), r => r
    .RunDelayed(TimeSpan.FromHours(1))
    .Then()
    .Every(15).Minutes(), ct);
```

### Add Specific Start Time

```csharp
// Start at specific time, then every hour
await dispatcher.Dispatch(new ScheduledTask(), r => r
    .RunAt(DateTimeOffset.UtcNow.AddDays(1).Date.AddHours(9))
    .Then()
    .Every(1).Hours(), ct);
```

### Add Run Now

```csharp
// Run immediately, then every hour
await dispatcher.Dispatch(new ImmediateTask(), r => r
    .RunNow()
    .Then()
    .Every(1).Hours(), ct);
```

### Add Constraints

```csharp
// Every 5 minutes for 6 months
await dispatcher.Dispatch(new ConstrainedTask(), r => r
    .Every(5).Minutes()
    .RunUntil(DateTimeOffset.UtcNow.AddMonths(6)), ct);

// Every hour, maximum 100 runs
await dispatcher.Dispatch(new ConstrainedTask(), r => r
    .Every(1).Hours()
    .MaxRuns(100), ct);

// Both constraints
await dispatcher.Dispatch(new ConstrainedTask(), r => r
    .Every(15).Minutes()
    .RunUntil(DateTimeOffset.UtcNow.AddDays(7))
    .MaxRuns(500), ct);
```

## Testing Considerations

**Interval Calculation**:
- `RecurringTask.CalculateNextRun()` is public - test directly
- `IInterval.GetNextOccurrence()` is public - test each interval type
- Use fixed `DateTimeOffset` values for deterministic tests

**Example Test**:
```csharp
var recurring = new RecurringTask
{
    MinuteInterval = new MinuteInterval(15) { OnSecond = 30 }
};

var now = new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero);
var nextRun = recurring.CalculateNextRun(now, 0); // First run
Assert.Equal(new DateTimeOffset(2024, 1, 1, 10, 15, 30, TimeSpan.Zero), nextRun);

nextRun = recurring.CalculateNextRun(nextRun.Value, 1); // Second run
Assert.Equal(new DateTimeOffset(2024, 1, 1, 10, 30, 30, TimeSpan.Zero), nextRun);
```

**Builder Testing**:
- Test fluent API transitions
- Test validation (e.g., `RunUntil` in past)
- Test interval precedence (Cron > others)

## Known Issues & Edge Cases

### 30-Second Gap Rule (`RecurringTask.cs:50-55`)

When using `RunNow`, `RunDelayed`, or `RunAt` with an interval:
- If initial runtime is before next interval time by < 30 seconds, initial run is skipped
- Prevents closely spaced executions (e.g., run at 10:00:29, then 10:01:00)

**Example**:
```csharp
// At 10:00:45, run now then every minute
recurring.RunNow().Then().Every(1).Minutes()
// First run: 10:00:45 (immediate)
// Second run: 10:01:45 (not 10:01:00 due to gap rule)
```

### Interval Cascade Behavior

Multiple intervals cascade - each refines the previous:
```csharp
recurring.Every(1).Months().OnDay(15) // 15th of month
    .Every(1).Days().At(9, 0)         // Adds: at 9:00 AM
    .Every(1).Hours().OnMinute(30)    // ERROR: conflicts with .At(9, 0)
```

**Best Practice**: Use only one interval type (second OR minute OR hour OR day OR month OR cron).

### Cron Expression Precedence

Cron expressions bypass all other intervals:
```csharp
var recurring = new RecurringTask
{
    CronInterval = new CronInterval("0 0 * * *"),
    MinuteInterval = new MinuteInterval(15) // Ignored
};
// Only cron expression is used
```

### MaxRuns and RunUntil

Both constraints apply:
```csharp
recurring.Every(1).Hours().RunUntil(future).MaxRuns(10)
// Stops after 10 runs OR when RunUntil is reached, whichever comes first
```

### Serialization Requirements

All interval types require parameterless constructors for JSON deserialization:
- Used in `WorkerService.ProcessPendingAsync()` (`WorkerService.cs:69`)
- All intervals have `//used for serialization/deserialization` comment

### Time Zone Handling

All times converted to UTC:
- `RecurringTask.CalculateNextRun()` converts `current` to UTC (line 25)
- `RunAt()` converts to UTC (`RecurringTaskBuilder.cs:21`)
- `RunUntil()` converts to UTC (`RecurringTaskBuilder.cs:37`)
- Cron expressions use UTC (`CronInterval.cs:30`)

## Performance Notes

**Calculation Complexity**:
- Simple intervals (second, minute, hour): O(1)
- Day intervals with `OnDays`: O(n) where n = days to check
- Month intervals with `OnFirst`: O(n) where n = days in month
- Cron expressions: O(1) via Cronos library

**Builder Allocation**:
- Fluent API creates multiple builder instances per configuration
- Short-lived objects, eligible for Gen 0 collection
- Consider caching `RecurringTask` instances for frequently used schedules

**Scheduler Impact**:
- Each recurring task adds one entry to `ConcurrentPriorityQueue`
- Priority queue lock contention increases with many recurring tasks
- Consider separate scheduler instances for high-frequency recurring tasks (requires custom implementation)
