using EverTask.Monitor.Api.DTOs.Auth;
using EverTask.Tests.Monitoring.TestHelpers;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EverTask.Tests.Monitoring.SignalR;

public class TokenExpirationTests
{
    [Fact]
    public async Task Should_abort_an_authenticated_hub_connection_when_its_token_expires()
    {
        var tokenService = new Mock<IJwtTokenService>();
        tokenService.Setup(service => service.ValidateToken("short-lived"))
                    .Returns(() => new TokenValidationResponse(
                        true, "signalr-user", DateTimeOffset.UtcNow.AddSeconds(2)));

        await using var factory = AuthenticatedFactory(tokenService.Object);
        using var client = factory.CreateClient();
        await using var connection = CreateConnection(factory, client.BaseAddress!, "short-lived");
        var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.Closed += _ =>
        {
            closed.TrySetResult(true);
            return Task.CompletedTask;
        };

        await connection.StartAsync();
        connection.State.ShouldBe(HubConnectionState.Connected);

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        connection.State.ShouldBe(HubConnectionState.Disconnected);
    }

    [Fact]
    public async Task Should_keep_an_authenticated_hub_connection_when_the_token_has_no_expiry()
    {
        var tokenService = new Mock<IJwtTokenService>();
        tokenService.Setup(service => service.ValidateToken("non-expiring"))
                    .Returns(new TokenValidationResponse(true, "signalr-user", null));

        await using var factory = AuthenticatedFactory(tokenService.Object);
        using var client = factory.CreateClient();
        await using var connection = CreateConnection(factory, client.BaseAddress!, "non-expiring");

        await connection.StartAsync();
        await Task.Delay(250);

        connection.State.ShouldBe(HubConnectionState.Connected);
    }

    private static MonitoringTestWebAppFactory AuthenticatedFactory(IJwtTokenService tokenService) =>
        new(requireAuthentication: true, configureServices: services =>
        {
            services.RemoveAll<IJwtTokenService>();
            services.AddSingleton(tokenService);
        });

    private static HubConnection CreateConnection(MonitoringTestWebAppFactory factory, Uri baseAddress,
                                                  string accessToken)
    {
        var url = new Uri(baseAddress, "/evertask-monitoring/hub");

        return new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            })
            .Build();
    }
}
