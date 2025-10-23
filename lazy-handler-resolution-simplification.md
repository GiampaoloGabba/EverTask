# Lazy Handler Resolution Simplification Plan

**Status**: Planning
**Created**: 2025-01-23
**Goal**: Simplify lazy handler resolution configuration and make it adaptive based on task scheduling patterns

## Problem Statement

The current lazy handler resolution system has several issues:

1. **Too many configuration options** that are confusing for users:
   - `UseLazyHandlerResolution` (bool)
   - `LazyHandlerResolutionThreshold` (TimeSpan, only for delayed tasks)
   - `AlwaysLazyForRecurring` (bool, ignores recurring interval)

2. **Inefficient for frequent recurring tasks**:
   - With `AlwaysLazyForRecurring = true` (default), a task running every 2 seconds recreates the handler 43,200 times/day
   - No benefit since the handler is used immediately after creation

3. **Rigid recurring logic**:
   - Binary flag `AlwaysLazyForRecurring` doesn't consider task interval
   - A task running every 10 seconds gets the same treatment as one running once a day

## Proposed Solution

### 1. Simplify Configuration (API)

**Before** (3 options, confusing):
```csharp
services.AddEverTask(opt => opt
    .SetUseLazyHandlerResolution(true)          // Global flag
    .SetLazyHandlerResolutionThreshold(TimeSpan.FromMinutes(30))  // Delayed only
    .SetAlwaysLazyForRecurring(false))          // Recurring only - confusing!
```

**After** (1 option, clear):
```csharp
// Default: lazy enabled with adaptive algorithm
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(...))

// Edge case: disable completely
services.AddEverTask(opt => opt.DisableLazyHandlerResolution())
```

### 2. Adaptive Algorithm (Internal Logic)

**Delayed Tasks** (unchanged, works well):
- Delay >= 30 minutes → lazy mode (save memory)
- Delay < 30 minutes → eager mode (execute soon anyway)

**Recurring Tasks** (NEW - adaptive):
- Calculate minimum interval dynamically (including cron expressions)
- Interval >= 5 minutes → lazy mode (long-lived, recreate each time)
- Interval < 5 minutes → eager mode (frequent, reuse handler)

**Cron Expressions** (NEW - smart detection):
- Calculate next two occurrences using Cronos library
- Use difference as minimum interval
- Example: `0 9 * * MON` → ~7 days → lazy mode ✅
- Example: `*/5 * * * *` → 5 minutes → eager mode ✅

### 3. Internal Thresholds (Not Configurable)

```csharp
// Hardcoded, sensible defaults
const TimeSpan DELAYED_LAZY_THRESHOLD = TimeSpan.FromMinutes(30);
const TimeSpan RECURRING_LAZY_THRESHOLD = TimeSpan.FromMinutes(5);
```

**Rationale**:
- Simplifies API surface
- Prevents user misconfiguration
- Values are well-balanced for most scenarios
- Can be made configurable later if needed (YAGNI)

## Implementation Tasks

### Phase 1: Core Logic Changes

#### 1.1 Add `GetMinimumInterval()` to `RecurringTask`

**File**: `src/EverTask/Scheduler/Recurring/RecurringTask.cs`

```csharp
/// <summary>
/// Calculates the minimum interval for this recurring task.
/// For cron expressions, calculates the interval between the next two occurrences.
/// For interval-based tasks, returns the configured interval.
/// </summary>
/// <returns>Minimum interval between executions</returns>
public TimeSpan GetMinimumInterval()
{
    // Cron: calculate interval between next two occurrences
    if (CronInterval != null && !string.IsNullOrEmpty(CronInterval.CronExpression))
    {
        var now = DateTimeOffset.UtcNow;
        var first = CronInterval.GetNextOccurrence(now);
        if (!first.HasValue) return TimeSpan.FromHours(1); // Fallback conservative

        var second = CronInterval.GetNextOccurrence(first.Value);
        if (!second.HasValue) return TimeSpan.FromHours(1); // Fallback conservative

        return second.Value - first.Value;
    }

    // Interval fields: use the most granular interval
    if (SecondInterval?.Interval > 0) return TimeSpan.FromSeconds(SecondInterval.Interval);
    if (MinuteInterval?.Interval > 0) return TimeSpan.FromMinutes(MinuteInterval.Interval);
    if (HourInterval?.Interval > 0) return TimeSpan.FromHours(HourInterval.Interval);
    if (DayInterval?.Interval > 0) return TimeSpan.FromDays(DayInterval.Interval);
    if (MonthInterval?.Interval > 0) return TimeSpan.FromDays(30); // Conservative approximation

    return TimeSpan.FromMinutes(5); // Safe default
}
```

**Tests to add**:
- `RecurringTaskTests.cs` → `Should_calculate_minimum_interval_for_second_based_recurring`
- `RecurringTaskTests.cs` → `Should_calculate_minimum_interval_for_minute_based_recurring`
- `RecurringTaskTests.cs` → `Should_calculate_minimum_interval_for_cron_daily` (should return ~24 hours)
- `RecurringTaskTests.cs` → `Should_calculate_minimum_interval_for_cron_frequent` (`*/5 * * * *` → 5 minutes)
- `RecurringTaskTests.cs` → `Should_calculate_minimum_interval_for_cron_weekly` (`0 9 * * MON` → ~7 days)
- `RecurringTaskTests.cs` → `Should_return_fallback_for_invalid_cron`

#### 1.2 Update `ShouldUseLazyResolution()` in Dispatcher

**File**: `src/EverTask/Dispatcher/Dispatcher.cs` (lines 214-233)

```csharp
/// <summary>
/// Determines if a task should use lazy handler resolution based on adaptive algorithm.
/// </summary>
/// <param name="executionTime">Scheduled execution time (null for immediate)</param>
/// <param name="recurring">Recurring task configuration (null for one-time)</param>
/// <returns>True if task should use lazy mode, false for eager mode</returns>
private bool ShouldUseLazyResolution(DateTimeOffset? executionTime, RecurringTask? recurring)
{
    // Feature disabled globally
    if (!serviceConfiguration.UseLazyHandlerResolution)
        return false;

    // Recurring tasks: adaptive based on interval
    if (recurring != null)
    {
        var minInterval = recurring.GetMinimumInterval();
        return minInterval >= TimeSpan.FromMinutes(5); // Internal threshold
    }

    // Delayed tasks: lazy if delay >= 30 minutes
    if (executionTime.HasValue)
    {
        var delay = executionTime.Value - DateTimeOffset.UtcNow;
        return delay >= TimeSpan.FromMinutes(30); // Internal threshold
    }

    // Immediate tasks: always eager
    return false;
}
```

**Tests to add**:
- `DispatcherTests.cs` → `Should_use_lazy_mode_for_recurring_with_long_interval` (>= 5 min)
- `DispatcherTests.cs` → `Should_use_eager_mode_for_recurring_with_short_interval` (< 5 min)
- `DispatcherTests.cs` → `Should_use_lazy_mode_for_daily_cron`
- `DispatcherTests.cs` → `Should_use_eager_mode_for_frequent_cron` (`*/2 * * * *`)
- `DispatcherTests.cs` → `Should_use_lazy_mode_for_delayed_over_threshold` (>= 30 min)
- `DispatcherTests.cs` → `Should_use_eager_mode_for_delayed_under_threshold` (< 30 min)
- `DispatcherTests.cs` → `Should_use_eager_mode_when_lazy_disabled_globally`

#### 1.3 Remove Obsolete Configuration Properties

**File**: `src/EverTask/MicrosoftExtensionsDI/EverTaskServiceConfiguration.cs`

**Remove** (lines 38-51):
```csharp
// ❌ DELETE THESE
public TimeSpan LazyHandlerResolutionThreshold { get; set; } = TimeSpan.FromHours(1);
public bool AlwaysLazyForRecurring { get; set; } = true;
```

**Remove methods** (lines 155-173):
```csharp
// ❌ DELETE THESE
public EverTaskServiceConfiguration SetLazyHandlerResolutionThreshold(TimeSpan threshold) { ... }
public EverTaskServiceConfiguration SetAlwaysLazyForRecurring(bool enabled) { ... }
```

**Keep and update**:
```csharp
/// <summary>
/// Enable adaptive lazy handler resolution for scheduled and recurring tasks.
/// When enabled, handlers are recreated at execution time based on task scheduling:
/// - Recurring tasks with intervals >= 5 minutes use lazy mode (memory efficient)
/// - Recurring tasks with intervals < 5 minutes use eager mode (performance efficient)
/// - Delayed tasks with delay >= 30 minutes use lazy mode
/// - Delayed tasks with delay < 30 minutes use eager mode
/// Default: true
/// </summary>
public bool UseLazyHandlerResolution { get; set; } = true;

/// <summary>
/// Disables lazy handler resolution completely.
/// Use only if lazy mode causes issues in your environment.
/// </summary>
public EverTaskServiceConfiguration DisableLazyHandlerResolution()
{
    UseLazyHandlerResolution = false;
    return this;
}
```

**Keep existing** (line 142-146):
```csharp
public EverTaskServiceConfiguration SetUseLazyHandlerResolution(bool enabled)
{
    UseLazyHandlerResolution = enabled;
    return this;
}
```

### Phase 2: Test Updates

#### 2.1 Update Existing Tests

**File**: `test/EverTask.Tests/IntegrationTests/LazyModeIntegrationTests.cs`

**Tests to UPDATE** (remove threshold configuration):

1. `Should_dispose_handler_after_execution_for_delayed_task_in_lazy_mode` (line 66-108)
   - **REMOVE**: `cfg.LazyHandlerResolutionThreshold = TimeSpan.FromSeconds(1);` (line 72)
   - **CHANGE**: Use delay >= 30 minutes instead of 2 seconds (line 83)
   - **REASON**: New threshold is 30 minutes, not configurable

2. `Should_not_dispose_handler_during_dispatch_in_lazy_mode_for_delayed_task` (line 161-211)
   - **REMOVE**: `cfg.LazyHandlerResolutionThreshold = TimeSpan.FromSeconds(1);` (line 171)
   - **CHANGE**: Use delay >= 30 minutes instead of 2 seconds (line 182)

**Tests to ADD**:

3. `Should_use_eager_mode_for_frequent_recurring` (NEW)
   - Recurring task every 2 seconds (< 5 min threshold)
   - Verify handler is NOT disposed between runs
   - Verify same handler instance reused

4. `Should_use_lazy_mode_for_infrequent_recurring` (NEW)
   - Recurring task every 10 minutes (>= 5 min threshold)
   - Verify handler IS disposed after each run
   - Verify new handler instance created each time

5. `Should_use_lazy_mode_for_cron_daily` (NEW)
   - Cron: `0 0 * * *` (daily at midnight)
   - Interval ~24 hours → lazy mode
   - Verify lazy behavior

6. `Should_use_eager_mode_for_cron_frequent` (NEW)
   - Cron: `*/2 * * * *` (every 2 minutes)
   - Interval < 5 minutes → eager mode
   - Verify eager behavior

7. `Should_use_eager_mode_for_short_delayed_tasks` (NEW)
   - Delayed task with 10 minute delay (< 30 min threshold)
   - Verify eager mode

#### 2.2 Add Unit Tests

**File**: `test/EverTask.Tests/RecurringTests/RecurringTaskTests.cs`

Add new test class or section for `GetMinimumInterval()`:

```csharp
public class RecurringTaskMinimumIntervalTests
{
    [Fact]
    public void Should_calculate_interval_for_second_recurring()
    {
        var task = new RecurringTask
        {
            SecondInterval = new SecondInterval(30)
        };

        task.GetMinimumInterval().ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Should_calculate_interval_for_minute_recurring()
    {
        var task = new RecurringTask
        {
            MinuteInterval = new MinuteInterval(15)
        };

        task.GetMinimumInterval().ShouldBe(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void Should_calculate_interval_for_daily_cron()
    {
        var task = new RecurringTask
        {
            CronInterval = new CronInterval("0 0 * * *") // Daily at midnight
        };

        var interval = task.GetMinimumInterval();
        interval.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromHours(23));
        interval.ShouldBeLessThanOrEqualTo(TimeSpan.FromHours(25)); // Allow DST variance
    }

    [Fact]
    public void Should_calculate_interval_for_frequent_cron()
    {
        var task = new RecurringTask
        {
            CronInterval = new CronInterval("*/5 * * * *") // Every 5 minutes
        };

        task.GetMinimumInterval().ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Should_calculate_interval_for_weekly_cron()
    {
        var task = new RecurringTask
        {
            CronInterval = new CronInterval("0 9 * * MON") // Mondays at 9 AM
        };

        var interval = task.GetMinimumInterval();
        interval.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromDays(6));
        interval.ShouldBeLessThanOrEqualTo(TimeSpan.FromDays(8));
    }

    [Fact]
    public void Should_return_fallback_for_invalid_cron()
    {
        var task = new RecurringTask
        {
            CronInterval = new CronInterval("invalid cron")
        };

        // Should throw on parse, but if we add error handling, test fallback
        Should.Throw<ArgumentException>(() => task.GetMinimumInterval());
    }

    [Fact]
    public void Should_prioritize_most_granular_interval()
    {
        var task = new RecurringTask
        {
            SecondInterval = new SecondInterval(30),
            MinuteInterval = new MinuteInterval(5) // Should be ignored
        };

        task.GetMinimumInterval().ShouldBe(TimeSpan.FromSeconds(30));
    }
}
```

### Phase 3: Documentation Updates

#### 3.1 Update Configuration Reference

**File**: `docs/configuration-reference.md`

**Find and UPDATE section** on lazy handler resolution:

```markdown
### Lazy Handler Resolution

**Default**: Enabled with adaptive algorithm

EverTask automatically optimizes memory usage for scheduled and recurring tasks by recreating
handlers at execution time instead of keeping them in memory. The system uses an adaptive
algorithm that considers task scheduling patterns:

- **Recurring tasks** with intervals >= 5 minutes use lazy mode (memory efficient)
- **Recurring tasks** with intervals < 5 minutes use eager mode (performance efficient)
- **Delayed tasks** with delay >= 30 minutes use lazy mode
- **Delayed tasks** with delay < 30 minutes use eager mode
- **Immediate tasks** always use eager mode

#### Disabling Lazy Mode

Only disable if lazy mode causes issues in your specific environment:

```csharp
services.AddEverTask(opt => opt
    .DisableLazyHandlerResolution())
```

**Note**: Internal thresholds (5 minutes for recurring, 30 minutes for delayed) are not
configurable. This prevents misconfiguration and ensures optimal behavior for most scenarios.
```

**REMOVE deprecated sections**:
- ❌ `LazyHandlerResolutionThreshold` documentation
- ❌ `AlwaysLazyForRecurring` documentation
- ❌ `SetLazyHandlerResolutionThreshold()` examples
- ❌ `SetAlwaysLazyForRecurring()` examples

#### 3.2 Update Configuration Cheatsheet

**File**: `docs/configuration-cheatsheet.md`

**UPDATE** lazy handler section:

```markdown
| Configuration | Default | Description |
|--------------|---------|-------------|
| `UseLazyHandlerResolution` | `true` | Enable adaptive lazy handler resolution (automatic optimization) |

**Disable lazy mode** (edge cases only):
```csharp
.DisableLazyHandlerResolution()
```
```

**REMOVE**:
- ❌ `LazyHandlerResolutionThreshold` row
- ❌ `AlwaysLazyForRecurring` row

#### 3.3 Update Advanced Features

**File**: `docs/advanced-features.md`

**Find section** on lazy handler resolution and UPDATE with new algorithm details:

```markdown
## Lazy Handler Resolution

EverTask uses an adaptive algorithm to optimize memory usage for scheduled and recurring tasks.

### How It Works

1. **Handler Creation**: During dispatch, a handler instance is created for validation only
2. **Lazy Mode Decision**: System calculates task's minimum interval or delay
3. **Handler Lifecycle**:
   - **Eager mode** (frequent tasks): Handler kept in memory until execution
   - **Lazy mode** (infrequent tasks): Handler released for GC, recreated at execution time

### Adaptive Algorithm

```
IF recurring task:
    interval = CalculateMinimumInterval(recurring)
    IF interval >= 5 minutes → LAZY
    ELSE → EAGER

ELSE IF delayed task:
    delay = executionTime - now
    IF delay >= 30 minutes → LAZY
    ELSE → EAGER

ELSE (immediate task):
    → EAGER
```

### Cron Expression Handling

For cron expressions, the minimum interval is calculated dynamically:

```csharp
// Daily cron: "0 0 * * *"
// Next occurrence: Tomorrow at midnight
// Second occurrence: Day after at midnight
// Interval: ~24 hours → LAZY mode ✅

// Frequent cron: "*/5 * * * *"
// Next occurrence: 5 minutes from now
// Second occurrence: 10 minutes from now
// Interval: 5 minutes → EAGER mode ✅
```

### Memory Impact

**Before** (with `AlwaysLazyForRecurring = true`):
- Task every 2 seconds → 43,200 handler creations/day ❌

**After** (adaptive algorithm):
- Task every 2 seconds → 1 handler creation, reused 43,200 times ✅
- Task every 1 hour → 24 handler creations/day ✅

### Disabling Lazy Mode

```csharp
services.AddEverTask(opt => opt
    .DisableLazyHandlerResolution())
```

Use only if:
- Debugging handler initialization issues
- Handler constructor has critical side effects
- Your handlers are singleton-like and benefit from long lifetime
```

**REMOVE**:
- ❌ Examples with `SetLazyHandlerResolutionThreshold()`
- ❌ Examples with `SetAlwaysLazyForRecurring()`
- ❌ Discussion of configurable thresholds

#### 3.4 Update Getting Started

**File**: `docs/getting-started.md`

**Find** basic configuration examples and ensure they DON'T mention removed options:

- ✅ Keep: Examples with default configuration (lazy enabled automatically)
- ❌ Remove: Any mentions of `SetLazyHandlerResolutionThreshold()` or `SetAlwaysLazyForRecurring()`

#### 3.5 Update CLAUDE.md Files

**Files to update**:
1. `src/EverTask/CLAUDE.md` (if it mentions lazy handler resolution)
2. `CLAUDE.md` (root, project overview)
3. `test/EverTask.Tests/CLAUDE.md` (if it mentions lazy mode tests)

**Search for**: `LazyHandlerResolution`, `AlwaysLazyForRecurring`, `LazyHandlerResolutionThreshold`

**Update** with new simplified configuration and adaptive algorithm details.

### Phase 4: Testing & Validation

#### 4.1 Unit Test Coverage

Run all tests to ensure no regressions:

```bash
# Run all core tests
dotnet test test/EverTask.Tests/EverTask.Tests.csproj

# Run specific test class
dotnet test test/EverTask.Tests/EverTask.Tests.csproj --filter FullyQualifiedName~LazyModeIntegrationTests
dotnet test test/EverTask.Tests/EverTask.Tests.csproj --filter FullyQualifiedName~RecurringTaskMinimumIntervalTests
```

**Expected results**:
- ✅ All existing tests pass (after updates)
- ✅ New `GetMinimumInterval()` tests pass
- ✅ New integration tests for adaptive algorithm pass
- ✅ Dispatcher tests for lazy/eager decision pass

#### 4.2 Integration Test Scenarios

**Test scenarios to verify manually** (if needed):

1. **Frequent recurring** (every 10 seconds for 5 minutes):
   - Verify handler reused (same instance ID across runs)
   - Verify no DisposeAsyncCore calls between runs
   - Verify single DisposeAsyncCore at end

2. **Infrequent recurring** (every 10 minutes, test with 30 minute delay):
   - Verify new handler each run (different instance IDs)
   - Verify DisposeAsyncCore called after each run

3. **Cron daily** (`0 0 * * *`):
   - Verify lazy mode detected
   - Verify handler released after dispatch

4. **Cron frequent** (`*/2 * * * *`):
   - Verify eager mode detected
   - Verify handler kept in memory

5. **Short delayed** (5 minute delay):
   - Verify eager mode
   - Verify handler not disposed during dispatch

6. **Long delayed** (2 hour delay):
   - Verify lazy mode
   - Verify handler released after dispatch

#### 4.3 Breaking Change Assessment

**Impact**: BREAKING CHANGE for users who configured thresholds

**Migration path**:
```csharp
// ❌ Before (will break compilation)
services.AddEverTask(opt => opt
    .SetLazyHandlerResolutionThreshold(TimeSpan.FromMinutes(30))
    .SetAlwaysLazyForRecurring(false))

// ✅ After (automatic, no configuration needed)
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(...))

// ✅ If they explicitly disabled lazy for recurring, now they disable entirely
services.AddEverTask(opt => opt
    .DisableLazyHandlerResolution())  // Closest equivalent to AlwaysLazyForRecurring = false
```

**Version bump**: This requires a MAJOR version bump (e.g., 1.x.x → 2.0.0) per SemVer.

#### 4.4 Performance Validation

**Benchmark** (optional, if time permits):

Create benchmark comparing:
- **Before**: Recurring every 2 seconds with `AlwaysLazyForRecurring = true`
- **After**: Recurring every 2 seconds with adaptive algorithm (should use eager)

Expected improvement:
- ~43,000 fewer handler allocations per day
- Reduced GC pressure
- Improved throughput for frequent recurring tasks

### Phase 5: Release Notes & Changelog

#### 5.1 CHANGELOG.md Entry

```markdown
## [2.0.0] - 2025-01-XX

### Breaking Changes

**Lazy Handler Resolution Simplification**

The lazy handler resolution system has been simplified with an adaptive algorithm that
automatically optimizes based on task scheduling patterns.

**Removed Configuration Options**:
- ❌ `LazyHandlerResolutionThreshold` - Now fixed at 30 minutes for delayed tasks
- ❌ `AlwaysLazyForRecurring` - Now adaptive based on recurring interval (5 minute threshold)
- ❌ `SetLazyHandlerResolutionThreshold()` method
- ❌ `SetAlwaysLazyForRecurring()` method

**Migration**:
- Remove calls to `SetLazyHandlerResolutionThreshold()` and `SetAlwaysLazyForRecurring()`
- Default behavior is now optimal for most scenarios (no configuration needed)
- To disable lazy mode entirely, use `.DisableLazyHandlerResolution()`

**New Features**:
- ✅ Adaptive algorithm for recurring tasks based on interval
- ✅ Smart cron expression interval detection
- ✅ Automatic eager mode for frequent tasks (< 5 min interval)
- ✅ Automatic lazy mode for infrequent tasks (>= 5 min interval)

**Performance Improvements**:
- Frequent recurring tasks (< 5 min) now reuse handlers instead of recreating
- Up to 43,000 fewer handler allocations per day for high-frequency tasks
- Reduced GC pressure and improved throughput

### Added
- `RecurringTask.GetMinimumInterval()` - Calculates minimum interval including cron expressions

### Changed
- Lazy handler resolution now uses adaptive algorithm instead of manual configuration
- Internal thresholds: 30 minutes for delayed, 5 minutes for recurring

### Removed
- Configuration properties: `LazyHandlerResolutionThreshold`, `AlwaysLazyForRecurring`
- Configuration methods: `SetLazyHandlerResolutionThreshold()`, `SetAlwaysLazyForRecurring()`
```

#### 5.2 Migration Guide

Create `docs/migration-v2.md`:

```markdown
# Migration Guide: v1.x → v2.0

## Lazy Handler Resolution Changes

### What Changed

The lazy handler resolution system has been simplified. Instead of manual configuration,
it now uses an adaptive algorithm that optimizes automatically.

### Before (v1.x)

```csharp
services.AddEverTask(opt => opt
    .SetLazyHandlerResolutionThreshold(TimeSpan.FromMinutes(30))
    .SetAlwaysLazyForRecurring(false))
```

### After (v2.0)

```csharp
// Default: automatic optimization (recommended)
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(...))

// If you explicitly disabled lazy mode for recurring tasks
services.AddEverTask(opt => opt.DisableLazyHandlerResolution())
```

### Behavior Changes

| Scenario | v1.x | v2.0 |
|----------|------|------|
| Recurring every 2 seconds | Lazy (configurable) | Eager (automatic) ✅ Better performance |
| Recurring every 10 minutes | Lazy (configurable) | Lazy (automatic) ✅ Same |
| Delayed 5 minutes | Eager/Lazy (configurable) | Eager (automatic) ✅ Better performance |
| Delayed 2 hours | Eager/Lazy (configurable) | Lazy (automatic) ✅ Same |
| Cron daily | Lazy (configurable) | Lazy (automatic) ✅ Same |
| Cron every 2 minutes | Lazy (configurable) | Eager (automatic) ✅ Better performance |

### Breaking Changes

**Compilation Errors**:
- `SetLazyHandlerResolutionThreshold()` method removed
- `SetAlwaysLazyForRecurring()` method removed
- `LazyHandlerResolutionThreshold` property removed
- `AlwaysLazyForRecurring` property removed

**Fix**: Remove these configuration calls. Default behavior is now optimal.

### Recommended Actions

1. **Remove manual configuration** - Let the adaptive algorithm handle it
2. **Test your recurring tasks** - Frequent tasks now perform better automatically
3. **Review custom thresholds** - If you had specific needs, file an issue to discuss
```

## Summary of Changes

### Code Changes
- ✅ Add `RecurringTask.GetMinimumInterval()` method
- ✅ Update `Dispatcher.ShouldUseLazyResolution()` with adaptive logic
- ✅ Remove `LazyHandlerResolutionThreshold` property and setter
- ✅ Remove `AlwaysLazyForRecurring` property and setter
- ✅ Update `UseLazyHandlerResolution` documentation
- ✅ Add `DisableLazyHandlerResolution()` method (already exists, update docs)

### Test Changes
- ✅ Update 2 existing lazy mode tests (remove threshold config, use 30+ min delays)
- ✅ Add 4 new integration tests (eager frequent, lazy infrequent, cron daily, cron frequent)
- ✅ Add 7 new unit tests for `GetMinimumInterval()`
- ✅ Add 7 new dispatcher tests for adaptive algorithm

### Documentation Changes
- ✅ Update `docs/configuration-reference.md`
- ✅ Update `docs/configuration-cheatsheet.md`
- ✅ Update `docs/advanced-features.md`
- ✅ Update `docs/getting-started.md` (remove obsolete examples)
- ✅ Update CLAUDE.md files (search for mentions)
- ✅ Add migration guide `docs/migration-v2.md`
- ✅ Update CHANGELOG.md with breaking changes

## Risks & Mitigations

### Risk 1: Breaking Changes for Users
**Mitigation**:
- Clear migration guide
- Major version bump (2.0.0)
- Default behavior is optimal for most scenarios

### Risk 2: Cron Interval Calculation Overhead
**Mitigation**:
- Calculation only happens once during dispatch
- Cronos library is efficient
- Result is used for decision, not stored/cached
- Acceptable trade-off for smart behavior

### Risk 3: Internal Thresholds May Not Fit All Scenarios
**Mitigation**:
- Values (5 min, 30 min) are well-balanced for most use cases
- Users can disable lazy mode entirely if needed
- Future: Make thresholds configurable if users request it (YAGNI for now)

## Open Questions

1. **Should we add logging** for lazy/eager decisions during dispatch?
   - Pro: Helps debugging, transparency
   - Con: Adds log noise
   - **Decision**: Add DEBUG-level log only

2. **Should we expose thresholds via advanced configuration**?
   - Pro: Power users can fine-tune
   - Con: More API surface, more testing
   - **Decision**: No, keep simple. Add later if needed (YAGNI)

3. **Should we cache `GetMinimumInterval()` result**?
   - Pro: Avoid recalculating cron interval
   - Con: Added complexity, memory overhead
   - **Decision**: No, calculation is cheap and happens once per dispatch

## Next Steps

1. Review and approve this plan
2. Implement Phase 1 (core logic changes)
3. Implement Phase 2 (test updates)
4. Implement Phase 3 (documentation updates)
5. Run full test suite
6. Create migration guide
7. Update CHANGELOG.md
8. Bump version to 2.0.0
9. Create release notes

---

**Estimated Effort**: 4-6 hours (including testing and documentation)
**Priority**: Medium (optimization, breaking change)
**Target Version**: 2.0.0
