namespace EverTask.Abstractions;

/// <summary>
/// Ambient access to the <see cref="ITaskExecutionContext"/> of the delivery running on the current
/// asynchronous flow — the way anything that is not the handler itself reads it.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a SINGLETON by <c>AddEverTask</c>, deliberately: an eager handler is resolved in the
/// dispatcher's scope, not the worker's, so its dependency graph is built long before the delivery starts and
/// a scoped accessor would hand those services an empty context forever. The value itself is per flow
/// (<c>AsyncLocal</c>), set right before the handler is invoked and cleared when the delivery ends.
/// </para>
/// <para>
/// <see cref="Current"/> is null outside a task execution — in a controller, a hosted service or a handler's
/// own constructor. Read it inside the call, never in a constructor.
/// </para>
/// </remarks>
public interface ITaskExecutionContextAccessor
{
    /// <summary>The context of the delivery running on this flow, or null when no task is running here.</summary>
    ITaskExecutionContext? Current { get; }
}
