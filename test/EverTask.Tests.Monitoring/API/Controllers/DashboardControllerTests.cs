using EverTask.Monitor.Api.DTOs.Dashboard;
using EverTask.Tests.Monitoring.TestHelpers;

namespace EverTask.Tests.Monitoring.API.Controllers;

public class DashboardControllerTests : MonitoringTestBase
{
    [Theory]
    [InlineData("Today")]
    [InlineData("Week")]
    [InlineData("Month")]
    [InlineData("All")]
    public async Task Should_get_overview_with_different_date_ranges(string range)
    {
        // Act
        var response = await Client.GetAsync($"/evertask-monitoring/api/dashboard/overview?range={range}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var overview = await DeserializeResponseAsync<OverviewDto>(response);
        overview.ShouldNotBeNull();
        overview.TotalTasksToday.ShouldBeGreaterThanOrEqualTo(0);
        overview.TotalTasksWeek.ShouldBeGreaterThanOrEqualTo(0);
        overview.FailedCount.ShouldBeGreaterThanOrEqualTo(0);
        overview.StatusDistribution.ShouldNotBeNull();
        overview.SuccessRate.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Should_get_recent_activity_with_limit()
    {
        // Arrange
        const int limit = 10;

        // Act
        var response = await Client.GetAsync($"/evertask-monitoring/api/dashboard/recent-activity?limit={limit}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var activities = await DeserializeResponseAsync<List<RecentActivityDto>>(response);
        activities.ShouldNotBeNull();
        activities.Count.ShouldBeLessThanOrEqualTo(limit);

        if (activities.Any())
        {
            var first = activities.First();
            first.TaskId.ShouldNotBe(Guid.Empty);
            first.Type.ShouldNotBeNullOrEmpty();
            first.Status.ShouldNotBe(default(QueuedTaskStatus));
        }
    }

    [Fact]
    public async Task Should_get_recent_activity_with_default_limit()
    {
        // Act
        var response = await Client.GetAsync("/evertask-monitoring/api/dashboard/recent-activity");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var activities = await DeserializeResponseAsync<List<RecentActivityDto>>(response);
        activities.ShouldNotBeNull();
        activities.Count.ShouldBeLessThanOrEqualTo(50); // Default limit
    }
}
