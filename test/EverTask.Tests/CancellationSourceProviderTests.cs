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
}
