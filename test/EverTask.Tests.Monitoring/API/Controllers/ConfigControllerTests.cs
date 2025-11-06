using EverTask.Tests.Monitoring.TestHelpers;

namespace EverTask.Tests.Monitoring.API.Controllers;

public class ConfigControllerTests : MonitoringTestBase
{
    [Fact]
    public async Task Should_get_config_without_authentication()
    {
        // Arrange - No auth header added

        // Act
        var response = await Client.GetAsync("/monitoring/api/config");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var config = await DeserializeResponseAsync<ConfigResponse>(response);
        config.ShouldNotBeNull();
        config.ApiBasePath.ShouldBe("/monitoring/api");
        config.UiBasePath.ShouldBe("/monitoring");
        config.SignalRHubPath.ShouldBe("/monitoring/hub");
        config.RequireAuthentication.ShouldBe(false);
        config.UiEnabled.ShouldBe(false);
    }
}

public record ConfigResponse(
    string ApiBasePath,
    string UiBasePath,
    string SignalRHubPath,
    bool RequireAuthentication,
    bool UiEnabled
);
