using EverTask.Tests.TestHelpers;

namespace EverTask.Tests;

public class CancellationSourceProviderTests
{
    private readonly CancellationSourceProvider _provider = new();

    [Fact]
    public void Should_create_cancellation_token()
    {
        var guid = TestGuidGenerator.New();
        var token = _provider.CreateToken(guid, CancellationToken.None);
        token.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public void Should_cancel_token()
    {
        var guid  = TestGuidGenerator.New();
        var token = _provider.CreateToken(guid, CancellationToken.None);
        _provider.CancelTokenForTask(guid);

        token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void Should_delete_cancellation_token_and_dispose_source()
    {
        var guid  = TestGuidGenerator.New();
        var token = _provider.CreateToken(guid, CancellationToken.None);
        _provider.Delete(guid);

        //handle should throw disposed exception
        Should.Throw<ObjectDisposedException>(() => token.WaitHandle);
    }

    [Fact]
    public void Should_complete_cancel_cleanup_when_cts_already_disposed()
    {
        // CU12: if the worker disposes the per-task CTS between the lookup and Cancel(), the
        // ObjectDisposedException must NOT propagate out of CancelTokenForTask — otherwise it aborts
        // the rest of Dispatcher.Cancel's cleanup (blacklist / unschedule / invalidate / remove) and
        // leaves the cancellation half-applied.
        var guid = TestGuidGenerator.New();
        _provider.CreateToken(guid, CancellationToken.None);

        // Dispose the source out-of-band (simulates the worker's Delete racing the cancel).
        _provider.TryGet(guid)!.Dispose();

        Should.NotThrow(() => _provider.CancelTokenForTask(guid));
    }
}
