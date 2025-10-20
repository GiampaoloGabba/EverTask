using EverTask.Tests.Monitoring.TestHelpers;

namespace EverTask.Tests.Monitoring.API.Controllers;

public class ConfigControllerTests : MonitoringTestBase
{
    [Fact]
    public async Task Should_get_config_without_authentication()
    {
        // Arrange - No auth header added

        // Act
        var response = await Client.GetAsync("/evertask/api/config");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var config = await DeserializeResponseAsync<ConfigResponse>(response);
        config.ShouldNotBeNull();
        config.ApiBasePath.ShouldBe("/evertask/api");
        config.UiBasePath.ShouldBe("/evertask");
        config.SignalRHubPath.ShouldBe("/evertask/monitor");
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
