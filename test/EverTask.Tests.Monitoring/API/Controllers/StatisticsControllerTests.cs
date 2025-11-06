using EverTask.Monitor.Api.DTOs.Statistics;
using EverTask.Tests.Monitoring.TestHelpers;

namespace EverTask.Tests.Monitoring.API.Controllers;

public class StatisticsControllerTests : MonitoringTestBase
{
    [Theory]
    [InlineData("Last7Days")]
    [InlineData("Last30Days")]
    [InlineData("Last90Days")]
    public async Task Should_get_success_rate_trend(string period)
    {
        // Act
        var response = await Client.GetAsync($"/evertask-monitoring/api/statistics/success-rate-trend?period={period}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var trend = await DeserializeResponseAsync<SuccessRateTrendDto>(response);
        trend.ShouldNotBeNull();
        trend.Timestamps.ShouldNotBeNull();
        trend.SuccessRates.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("Today")]
    [InlineData("Week")]
    [InlineData("Month")]
    [InlineData("All")]
    public async Task Should_get_task_type_distribution(string range)
    {
        // Act
        var response = await Client.GetAsync($"/evertask-monitoring/api/statistics/task-types?range={range}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var distribution = await DeserializeResponseAsync<Dictionary<string, int>>(response);
        distribution.ShouldNotBeNull();
        distribution.Count.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData("Today")]
    [InlineData("Week")]
    [InlineData("Month")]
    public async Task Should_get_execution_times(string range)
    {
        // Act
        var response = await Client.GetAsync($"/evertask-monitoring/api/statistics/execution-times?range={range}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var executionTimes = await DeserializeResponseAsync<List<ExecutionTimeDto>>(response);
        executionTimes.ShouldNotBeNull();
        // Execution times might be empty if no completed tasks in the range
    }
}
