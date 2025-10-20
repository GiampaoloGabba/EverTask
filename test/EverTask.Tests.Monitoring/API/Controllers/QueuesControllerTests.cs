using EverTask.Tests.Monitoring.TestHelpers;

namespace EverTask.Tests.Monitoring.API.Controllers;

public class QueuesControllerTests : MonitoringTestBase
{
    [Fact]
    public async Task Should_get_all_queues_metrics()
    {
        // Act
        var response = await Client.GetAsync("/evertask/api/queues");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var queues = await DeserializeResponseAsync<List<QueueMetricsDto>>(response);
        queues.ShouldNotBeNull();
        queues.Count.ShouldBeGreaterThan(0);

        var defaultQueue = queues.FirstOrDefault(q => q.QueueName == "default");
        defaultQueue.ShouldNotBeNull();
        defaultQueue.TotalTasks.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Should_get_tasks_in_specific_queue()
    {
        // Arrange
        const string queueName = "default";
        const int pageSize = 10;

        // Act
        var response = await Client.GetAsync($"/evertask/api/queues/{queueName}/tasks?page=1&pageSize={pageSize}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var pagedResponse = await DeserializeResponseAsync<TasksPagedResponse>(response);
        pagedResponse.ShouldNotBeNull();
        pagedResponse.Items.ShouldNotBeNull();

        foreach (var task in pagedResponse.Items)
        {
            task.QueueName.ShouldBe(queueName);
        }
    }
}
