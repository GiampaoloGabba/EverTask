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

The fix introduces a new extension method `CalculateNextValidRun` that encapsulates the skip logic:

1. Retrieve the task's original scheduled execution time (`task.ExecutionTime`)
2. Call `CalculateNextValidRun` extension method which:
   - Calculates next occurrence based on **scheduled time**, not current time
   - Checks if calculated next run is in the past
   - If in the past, iteratively calculates forward until finding a future time
   - Collects all skipped occurrences in a list
   - Includes safety limit to prevent infinite loops (max 1000 iterations)
   - Returns `NextRunResult` containing next run time, skip count, and skipped times
3. If occurrences were skipped:
   - Log detailed information about skipped times
   - Persist skip information to `RunsAudit` table (if using EfCore storage)
4. Update the task's next run time
5. Schedule the next occurrence

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

### Audit Trail for Skipped Occurrences

All storage implementations (EfCore, Memory) persist skipped occurrences to the `RunsAudit` collection:

**How It Works:**
- When a recurring task skips missed occurrences, a special `RunsAudit` entry is created
- The entry uses `QueuedTaskStatus.Completed` as the status
- The `Exception` field contains skip details: `"Skipped N missed occurrence(s) to maintain schedule: [times]"`
- This provides a permanent audit trail of skipped executions
- **All implementations** of `ITaskStorage` support this via the `RecordSkippedOccurrences` method

**Querying Skipped Occurrences:**
```csharp
// Find all skip audit entries for a task
var skipAudits = task.RunsAudits
    .Where(a => a.Exception != null && a.Exception.Contains("Skipped"))
    .ToList();

// Count total skipped occurrences
var totalSkipped = skipAudits
    .Sum(a => ExtractSkipCount(a.Exception));
```

**Benefits:**
- Permanent record of system downtime impact
- Distinguish between "never ran" and "intentionally skipped"
- Debugging and monitoring visibility
- Audit compliance for scheduled jobs

**Implementation:**
- `EfCoreTaskStorage`: Persists to database `RunsAudit` table
- `MemoryTaskStorage`: Stores in in-memory `RunsAudit` collection
- Custom implementations: Must implement `RecordSkippedOccurrences` from `ITaskStorage`

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

The fix includes comprehensive test coverage:

### Unit Tests (`RecurringTaskScheduleDriftTests.cs`)

1. **Schedule Maintenance**: Verifies tasks maintain schedule across different interval types
2. **No Drift Behavior**: Tests that delayed execution doesn't cause drift
3. **Extension Method**: Tests for `CalculateNextValidRun` extension method
4. **Skip Detection**: Verifies skip count and skipped occurrence tracking
5. **Infinite Loop Protection**: Tests max iteration safety limit
6. **Edge Cases**: MaxRuns, RunUntil, null handling, chronological ordering

### Integration Tests (`RecurringTaskSkipPersistenceTests.cs`)

1. **Persistence Verification**: Confirms skipped occurrences are saved to `RunsAudit`
2. **Audit Entry Format**: Validates skip message format and status
3. **No-Skip Scenarios**: Ensures no audit when nothing is skipped
4. **Error Handling**: Tests graceful handling of nonexistent tasks
5. **Storage Compatibility**: Verifies EfCore vs MemoryStorage behavior

**Running Tests:**
```bash
# Run all schedule drift tests
dotnet test --filter "FullyQualifiedName~RecurringTaskScheduleDrift"

# Run persistence tests
dotnet test --filter "FullyQualifiedName~RecurringTaskSkipPersistence"
```

## References

- **Issue**: Schedule drift in recurring tasks after delays
- **Core Fix**: `src/EverTask/Worker/WorkerExecutor.cs` - `QueueNextOccourrence` method
- **Extension Method**: `src/EverTask/Scheduler/Recurring/RecurringTaskExtensions.cs`
- **Result Record**: `src/EverTask/Scheduler/Recurring/NextRunResult.cs`
- **Persistence**: `src/Storage/EverTask.Storage.EfCore/EfCoreTaskStorage.cs` - `RecordSkippedOccurrences` method
- **Unit Tests**: `test/EverTask.Tests/RecurringTests/RecurringTaskScheduleDriftTests.cs`
- **Integration Tests**: `test/EverTask.Tests/IntegrationTests/RecurringTaskSkipPersistenceTests.cs`
