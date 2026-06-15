using EverTask.Abstractions;

namespace EverTask.Tests;

/// <summary>
/// CU17/L32 + L27: eager handlers are resolved from the singleton dispatcher's ROOT provider and
/// carried to execution. The worker disposes them after execution while the root container disposes
/// the SAME tracked transient again at shutdown (double-dispose), and the instances stay pinned in the
/// root's disposables list until shutdown (root-pinning leak). The fixes are: (1) DisposeAsync is
/// idempotent so DisposeAsyncCore runs at most once however many times the handler is disposed;
/// (2) the eager handler is resolved in an EverTask-owned scope that is disposed right after execution,
/// so nothing is pinned in the root container.
/// </summary>
public class EagerHandlerLifecycleTests
{
    public record SomeTask : IEverTask;

    private sealed class CountingHandler : EverTaskHandler<SomeTask>
    {
        public int DisposeCoreCalls;

        public override Task Handle(SomeTask backgroundTask, CancellationToken cancellationToken) => Task.CompletedTask;

        protected override ValueTask DisposeAsyncCore()
        {
            Interlocked.Increment(ref DisposeCoreCalls);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Should_run_dispose_core_once_when_handler_disposed_multiple_times()
    {
        var handler = new CountingHandler();

        // An eager handler can be disposed by the worker after execution AND again by the root
        // container at shutdown (and once per occurrence for a reused recurring eager instance).
        await ((IAsyncDisposable)handler).DisposeAsync();
        await ((IAsyncDisposable)handler).DisposeAsync();
        await ((IAsyncDisposable)handler).DisposeAsync();

        handler.DisposeCoreCalls.ShouldBe(1,
            "DisposeAsyncCore must run at most once no matter how many times the handler is disposed (CU17/L32)");
    }
}
