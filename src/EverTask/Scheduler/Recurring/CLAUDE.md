# Recurring Task Scheduler

## Purpose

Fluent builder API + interval calculation for recurring tasks. Supports second/minute/hour/day/month intervals plus cron expressions.

## Key Components

| Component | Responsibility |
|-----------|----------------|
| `RecurringTask.cs` | Immutable config + scheduling logic (`CalculateNextRun`, `GetNextOccurrence`) |
| `Builder/RecurringTaskBuilder.cs` | Entry point for fluent API |
| Interval classes | `SecondInterval`, `MinuteInterval`, `HourInterval`, `DayInterval`, `MonthInterval`, `CronInterval` |

**Cron Expression Support**: Uses [Cronos](https://github.com/HangfireIO/Cronos) library (validate expressions at https://crontab.guru/).

## Critical Gotchas

### 1. Cron Expression Precedence
**CRITICAL**: If `CronInterval` is set, ALL other intervals (Second/Minute/Hour/Day/Month) are **ignored**.

**Location**: `RecurringTask.GetNextOccurrence()` checks Cron first.

### 2. 30-Second Gap Rule
When first run falls before calculated next interval, a **30-second buffer** is added to prevent overlapping executions.

**Example**: Task starts 10:00:25, interval "every minute" → next run 10:01:30 (not 10:01:00).

**Location**: `RecurringTask.CalculateNextRun()` when `currentRun > 0`.

### 3. Interval Cascade
Multiple intervals refine each other: Month → Day → Hour → Minute → Second.

**Example**: `.Every(5).Minutes().AtSecond(30)` = Every 5 minutes at :30 seconds mark.

### 4. Serialization Requirement
**ALL interval classes MUST have parameterless constructor** for Newtonsoft.Json (RecurringTask is persisted to DB).

**Check**: Verify `public XInterval() { }` in all interval classes.

### 5. Stop Conditions
Tasks auto-stop when:
- `MaxRuns` reached (`.MaxRuns(10)` stops after 10 executions)
- `RunUntil` exceeded (`.RunUntil(endDate)` stops after date)

**Location**: `RecurringTask.CalculateNextRun()` returns `null` to signal termination.

## 🔗 Test Coverage

**When modifying interval calculation**:
- Critical test: `test/EverTask.Tests/RecurringTests/RecurringTaskScheduleDriftTests.cs` (validates gap rule)
- Update: `test/EverTask.Tests/RecurringTests/Intervals/`

**When modifying fluent builder**:
- Update: `test/EverTask.Tests/RecurringTests/Builders/`
- Chain tests: `test/EverTask.Tests/RecurringTests/Builders/Chains/`

**When adding new interval type**:
- Add parameterless constructor
- Add calculation tests in `test/EverTask.Tests/RecurringTests/Intervals/`
- Add builder tests in `test/EverTask.Tests/RecurringTests/Builders/`
