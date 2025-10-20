# Recurring Task Schedule Drift - Problem and Solution

## Overview

This document describes a critical fix to the recurring task scheduling mechanism in EverTask that prevents schedule drift when tasks are delayed or when the system experiences downtime.

## The Problem

### Background

When a recurring task completes execution, the system needs to calculate the next occurrence time to schedule the subsequent execution. The original implementation had a fundamental flaw in how it calculated this next occurrence.

### Original Behavior

The original implementation in `WorkerExecutor.QueueNextOccourrence` calculated the next run time using the **current system time** (`DateTimeOffset.UtcNow`) as the base:

```csharp
var nextRun = task.RecurringTask.CalculateNextRun(DateTimeOffset.UtcNow, currentRun + 1);
```

### The Schedule Drift Issue

This approach caused **schedule drift** - the gradual shift of execution times away from their intended schedule. Here's why:

**Example Scenario:**
- A task is configured to run every hour at :00 minutes (1:00, 2:00, 3:00, 4:00, ...)
- Task is scheduled for 2:00 PM
- Due to system load or queue congestion, the task actually executes at 2:45 PM
- Next run calculation: `CalculateNextRun(2:45 PM)` → Next execution at 3:45 PM ❌
- Over time, the task drifts further from the intended :00 schedule

**Impact:**
1. **Predictability Loss**: Tasks no longer run at their configured times
2. **Accumulating Drift**: Each delayed execution compounds the problem
3. **Schedule Chaos**: After downtime, tasks could be severely off-schedule
4. **Missed Execution Attempts**: If the system was down, it might try to execute all missed occurrences

### Visual Example

```
Intended Schedule:  1:00  2:00  3:00  4:00  5:00  6:00
Actual Execution:   1:00  2:45  3:50  4:55  6:05  7:15  ← Drift accumulates
                          ↑     ↑     ↑     ↑     ↑
                         +45   +50   +55   +65   +75 minutes drift
```

## The Solution

### New Behavior

The fix changes the base time for next occurrence calculation from the **current time** to the **scheduled execution time**:

```csharp
// Use the time the task was scheduled for, not when it actually ran
var scheduledTime = task.ExecutionTime ?? DateTimeOffset.UtcNow;
var nextRun = task.RecurringTask.CalculateNextRun(scheduledTime, currentRun + 1);

// If next run is in the past, skip forward to next valid occurrence
while (nextRun.HasValue && nextRun.Value < DateTimeOffset.UtcNow && maxSkips-- > 0)
{
    nextRun = task.RecurringTask.CalculateNextRun(nextRun.Value, currentRun + 1);
}
```

### Key Improvements

1. **No Schedule Drift**: Calculations are based on the intended schedule, not actual execution time
2. **Skip Missed Executions**: If the next calculated time is in the past, the system automatically advances to the next valid occurrence
3. **Downtime Recovery**: After system downtime, tasks resume at their next valid scheduled time instead of trying to catch up
4. **Predictable Behavior**: Tasks maintain their configured rhythm regardless of execution delays

### Corrected Example

```
Intended Schedule:  1:00  2:00  3:00  4:00  5:00  6:00
Actual Execution:   1:00  2:45  3:00  4:00  5:00  6:00  ← Maintains schedule
                          ↑     ↑     ↑     ↑     ↑
                      Delayed but Uses Uses Uses Uses
                      but next uses  3:00  4:00  5:00  6:00
                      calculates  not as base not not not
                      from 2:00   2:45       drift drift drift
```

### After System Downtime

**Scenario**: System down from 2:00 PM to 5:30 PM, task configured to run hourly

**Old Behavior**:
- Might try to execute all missed runs (2:00, 3:00, 4:00, 5:00)
- Or start from current time and drift the schedule

**New Behavior**:
- Calculates next from last scheduled time (2:00)
- Sees 3:00, 4:00, 5:00 are all in the past
- Skips to next valid time: 6:00 PM ✓
- Maintains original schedule

## Technical Details

### Algorithm

1. Retrieve the task's original scheduled execution time (`task.ExecutionTime`)
2. Calculate next occurrence based on **scheduled time**, not current time
3. Check if calculated next run is in the past
4. If in the past, iteratively calculate forward until finding a future time
5. Include safety limit to prevent infinite loops (max 1000 iterations)
6. Log when skipping missed occurrences for visibility

### Edge Cases Handled

1. **First Execution**: Falls back to `UtcNow` if no scheduled time exists
2. **Infinite Loop Protection**: Maximum 1000 iterations before stopping
3. **MaxRuns Limit**: Respects the configured maximum execution count
4. **RunUntil Limit**: Respects the configured end date/time
5. **Invalid Configuration**: Stops gracefully if no valid future occurrence exists

### Performance Considerations

- The while loop typically runs 0-1 times under normal operation
- Only executes multiple iterations after significant downtime
- Maximum 1000 iterations provides safety without performance impact
- Logging provides visibility without affecting hot path

## Migration Notes

### Behavioral Changes

Applications using EverTask will notice:

1. **More Predictable Schedules**: Recurring tasks stay on their configured schedule
2. **No Catch-Up Executions**: After downtime, tasks skip missed runs
3. **Better Long-Running Task Handling**: Tasks with longer execution times won't drift

### Compatibility

This is a **behavioral change** but maintains API compatibility:
- No breaking API changes
- Existing configurations work as-is
- Serialized tasks in storage remain compatible
- Only the runtime scheduling behavior changes

### Recommended Actions

1. **Review Critical Tasks**: Verify that skipping missed executions is acceptable for your use case
2. **Update Monitoring**: Watch for "skipped missed occurrences" log messages
3. **Test After Downtime**: Validate behavior after maintenance windows

## Testing

The fix includes comprehensive unit tests covering:

1. Normal execution without delays
2. Delayed execution maintaining schedule
3. Multiple missed occurrences (skip-ahead behavior)
4. Infinite loop protection
5. MaxRuns and RunUntil boundary conditions
6. Different interval types (seconds, minutes, hours, cron)

See `RecurringTaskScheduleDriftTests.cs` for complete test coverage.

## References

- Issue: Schedule drift in recurring tasks after delays
- Fix Location: `src/EverTask/Worker/WorkerExecutor.cs` - `QueueNextOccourrence` method
- Test Location: `test/EverTask.Tests/RecurringTests/RecurringTaskScheduleDriftTests.cs`
