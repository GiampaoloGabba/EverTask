using System.Collections.Concurrent;

namespace EverTask.Tests.TestHelpers;

/// <summary>
/// Thread-safe state manager for test tasks to replace static properties
/// Allows isolated state tracking per test without pollution between parallel tests
/// </summary>
public class TestTaskStateManager
{
    private readonly ConcurrentDictionary<string, TaskExecutionState> _states = new();

    /// <summary>
    /// Records that a task has started execution
    /// </summary>
    public void RecordStart(string taskKey)
    {
        _states.AddOrUpdate(
            taskKey,
            _ => new TaskExecutionState { StartTime = DateTimeOffset.UtcNow, ExecutionCount = 1 },
            (_, state) =>
            {
                state.StartTime = DateTimeOffset.UtcNow;
                state.ExecutionCount++;
                return state;
            });
    }

    /// <summary>
    /// Records that a task has completed execution
    /// </summary>
    public void RecordCompletion(string taskKey)
    {
        _states.AddOrUpdate(
            taskKey,
            _ => new TaskExecutionState { EndTime = DateTimeOffset.UtcNow },
            (_, state) =>
            {
                state.EndTime = DateTimeOffset.UtcNow;
                return state;
            });
    }

    /// <summary>
    /// Increments the execution counter for a task
    /// </summary>
    public void IncrementCounter(string taskKey)
    {
        _states.AddOrUpdate(
            taskKey,
            _ => new TaskExecutionState { ExecutionCount = 1 },
            (_, state) =>
            {
                state.ExecutionCount++;
                return state;
            });
    }

    /// <summary>
    /// Gets the execution count for a specific task
    /// </summary>
    public int GetCounter(string taskKey)
    {
        return _states.TryGetValue(taskKey, out var state) ? state.ExecutionCount : 0;
    }

    /// <summary>
    /// Gets the start time for a specific task
    /// </summary>
    public DateTimeOffset? GetStartTime(string taskKey)
    {
        return _states.TryGetValue(taskKey, out var state) ? state.StartTime : null;
    }

    /// <summary>
    /// Gets the end time for a specific task
    /// </summary>
    public DateTimeOffset? GetEndTime(string taskKey)
    {
        return _states.TryGetValue(taskKey, out var state) ? state.EndTime : null;
    }

    /// <summary>
    /// Gets the full state for a specific task
    /// </summary>
    public TaskExecutionState? GetState(string taskKey)
    {
        return _states.TryGetValue(taskKey, out var state) ? state : null;
    }

    /// <summary>
    /// Resets the state for a specific task
    /// </summary>
    public void Reset(string taskKey)
    {
        _states.TryRemove(taskKey, out _);
    }

    /// <summary>
    /// Resets all task states
    /// </summary>
    public void ResetAll()
    {
        _states.Clear();
    }

    /// <summary>
    /// Checks if two tasks executed in parallel (overlapping time windows)
    /// </summary>
    public bool WereExecutedInParallel(string taskKey1, string taskKey2)
    {
        var state1 = GetState(taskKey1);
        var state2 = GetState(taskKey2);

        if (state1?.StartTime == null || state1.EndTime == null ||
            state2?.StartTime == null || state2.EndTime == null)
        {
            return false;
        }

        // Check if time windows overlap
        return state1.StartTime < state2.EndTime && state2.StartTime < state1.EndTime;
    }
}

/// <summary>
/// Represents the execution state of a test task
/// </summary>
public class TaskExecutionState
{
    public DateTimeOffset? StartTime { get; set; }
    public DateTimeOffset? EndTime { get; set; }
    public int ExecutionCount { get; set; }
    public Dictionary<string, object> CustomData { get; set; } = new();
}
