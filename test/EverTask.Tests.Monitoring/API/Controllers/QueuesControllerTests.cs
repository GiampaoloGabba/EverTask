using EverTask.Tests.Monitoring.TestHelpers;

namespace EverTask.Tests.Monitoring.API.Controllers;

public class QueuesControllerTests : MonitoringTestBase
{
    [Fact]
    public async Task Should_get_all_queues_metrics()
    {
        // Act
        var response = await Client.GetAsync("/evertask-monitoring/api/queues");

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
        var response = await Client.GetAsync($"/evertask-monitoring/api/queues/{queueName}/tasks?page=1&pageSize={pageSize}");

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

    [Fact]
    public async Task Should_get_queue_configurations_with_all_configured_queues()
    {
        // Act
        var response = await Client.GetAsync("/evertask-monitoring/api/queues/configurations");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var configurations = await DeserializeResponseAsync<List<QueueConfigurationDto>>(response);
        configurations.ShouldNotBeNull();
        configurations.Count.ShouldBeGreaterThan(0);

        // Should include the default queue
        var defaultQueue = configurations.FirstOrDefault(q => q.QueueName == "default");
        defaultQueue.ShouldNotBeNull();
        defaultQueue.MaxDegreeOfParallelism.ShouldBeGreaterThan(0);
        defaultQueue.ChannelCapacity.ShouldBeGreaterThan(0);
        defaultQueue.QueueFullBehavior.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Should_include_queues_without_tasks()
    {
        // Act
        var response = await Client.GetAsync("/evertask-monitoring/api/queues/configurations");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var configurations = await DeserializeResponseAsync<List<QueueConfigurationDto>>(response);
        configurations.ShouldNotBeNull();

        // Should include queues even if they have no tasks
        var queuesWithoutTasks = configurations.Where(q => q.TotalTasks == 0).ToList();
        queuesWithoutTasks.ShouldNotBeEmpty();

        // Each queue should still have valid configuration
        foreach (var queue in queuesWithoutTasks)
        {
            queue.MaxDegreeOfParallelism.ShouldBeGreaterThan(0);
            queue.ChannelCapacity.ShouldBeGreaterThan(0);
            queue.QueueFullBehavior.ShouldNotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task Should_merge_configuration_with_metrics()
    {
        // Act
        var response = await Client.GetAsync("/evertask-monitoring/api/queues/configurations");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var configurations = await DeserializeResponseAsync<List<QueueConfigurationDto>>(response);
        configurations.ShouldNotBeNull();

        var queueWithTasks = configurations.FirstOrDefault(q => q.TotalTasks > 0);
        queueWithTasks.ShouldNotBeNull();

        // Should have both configuration and metrics
        queueWithTasks.MaxDegreeOfParallelism.ShouldBeGreaterThan(0);
        queueWithTasks.ChannelCapacity.ShouldBeGreaterThan(0);
        queueWithTasks.QueueFullBehavior.ShouldNotBeNullOrEmpty();
        queueWithTasks.TotalTasks.ShouldBeGreaterThan(0);
        (queueWithTasks.PendingTasks + queueWithTasks.InProgressTasks +
         queueWithTasks.CompletedTasks + queueWithTasks.FailedTasks).ShouldBe(queueWithTasks.TotalTasks);
    }
}
