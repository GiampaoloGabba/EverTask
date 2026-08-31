namespace EverTask.Worker;

/// <summary>
/// The ambient <see cref="ITaskExecutionContext"/> of the delivery running on the current asynchronous flow.
/// </summary>
/// <remarks>
/// <para>
/// The value lives in a STATIC <see cref="AsyncLocal{T}"/>: the accessor is registered as a singleton, but the
/// value is per flow, so two deliveries running side by side — in the same host or in two hosts sharing a
/// process, as the tests do — never see each other's context. Keeping the storage static also means the worker
/// does not have to be handed the accessor instance to publish into it.
/// </para>
/// <para>
/// Set by <c>WorkerExecutor</c> right before the handler is invoked and cleared when the delivery ends. The
/// clear matters for the fully synchronous case only: when the delivery suspends at least once, the mutation
/// stays inside its own flow and is discarded with it.
/// </para>
/// </remarks>
internal sealed class AmbientTaskExecutionContextAccessor : ITaskExecutionContextAccessor
{
    private static readonly AsyncLocal<ITaskExecutionContext?> Ambient = new();

    public ITaskExecutionContext? Current => Ambient.Value;

    internal static void Set(ITaskExecutionContext? context) => Ambient.Value = context;
}
