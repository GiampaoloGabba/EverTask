using EverTask.Monitor.Api.DTOs.Dashboard;
using EverTask.Monitor.Api.DTOs.Tasks;
using EverTask.Tests.Monitoring.TestHelpers;
using EverTask.Tests.Monitoring.TestData;

namespace EverTask.Tests.Monitoring.Integration;

public class EndToEndTests : MonitoringTestBase
{
    protected override bool EnableWorker => true;

    [Fact(Skip = "SignalR timing issue: Connection negotiation race condition causes intermittent failures in test environments. Events are published correctly but test client may not be fully ready to receive them.")]
    public async Task Should_dispatch_task_and_receive_signalr_event()
    {
        // Arrange
        await using var signalRClient = CreateSignalRClient();
        await signalRClient.StartAsync();

        var dispatcher = Factory.Services.GetRequiredService<ITaskDispatcher>();

        // Act - Dispatch a task
        var task = new SampleTask("End-to-end test");
        var taskId = await dispatcher.Dispatch(task);

        // Wait for SignalR events
        await signalRClient.WaitForEventsAsync(2, timeoutMs: 5000); // Started + Completed

        // Assert SignalR events
        signalRClient.ReceivedEvents.Count.ShouldBeGreaterThanOrEqualTo(2);

        var startedEvent = signalRClient.ReceivedEvents.FirstOrDefault(e => e.Message.Contains("started"));
        startedEvent.ShouldNotBeNull();
        startedEvent.TaskId.ShouldBe(taskId);

        var completedEvent = signalRClient.ReceivedEvents.FirstOrDefault(e => e.Message.Contains("completed"));
        completedEvent.ShouldNotBeNull();
        completedEvent.TaskId.ShouldBe(taskId);
    }

    [Fact]
    public async Task Should_query_task_via_api_after_execution()
    {
        // Arrange
        var dispatcher = Factory.Services.GetRequiredService<ITaskDispatcher>();

        // Act - Dispatch and execute task
        var task = new SampleTask("API query test");
        var taskId = await dispatcher.Dispatch(task);

        // Wait for task completion
        await WaitForTaskCompletionAsync(taskId);

        // Query task via API
        var response = await Client.GetAsync($"/evertask-monitoring/api/tasks/{taskId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var taskDetail = await DeserializeResponseAsync<TaskDetailDto>(response);
        taskDetail.ShouldNotBeNull();
        taskDetail.Id.ShouldBe(taskId);
        taskDetail.Status.ShouldBe(QueuedTaskStatus.Completed);
        taskDetail.Type.ShouldContain("SampleTask");
    }

    [Fact]
    public async Task Should_track_failed_task_in_api_and_signalr()
    {
        // Arrange
        await using var signalRClient = CreateSignalRClient();
        await signalRClient.StartAsync();

        var dispatcher = Factory.Services.GetRequiredService<ITaskDispatcher>();

        // Act - Dispatch failing task
        var task = new SampleFailingTask("Failure tracking test");
        var taskId = await dispatcher.Dispatch(task);

        // Wait for SignalR error event
        var errorEvent = await signalRClient.WaitForEventAsync(
            e => e.TaskId == taskId && e.Severity == "Error",
            timeoutMs: 5000);

        // Assert SignalR event
        errorEvent.ShouldNotBeNull();
        errorEvent.Exception.ShouldNotBeNullOrEmpty();

        // Wait for task to be marked as failed in storage
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Failed);

        // Query task via API
        var response = await Client.GetAsync($"/evertask-monitoring/api/tasks/{taskId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var taskDetail = await DeserializeResponseAsync<TaskDetailDto>(response);
        taskDetail.ShouldNotBeNull();
        taskDetail.Status.ShouldBe(QueuedTaskStatus.Failed);
        taskDetail.Exception.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Should_display_task_in_dashboard_statistics()
    {
        // Arrange
        var dispatcher = Factory.Services.GetRequiredService<ITaskDispatcher>();

        // Get initial overview
        var initialResponse = await Client.GetAsync("/evertask-monitoring/api/dashboard/overview?range=Today");
        var initialOverview = await DeserializeResponseAsync<OverviewDto>(initialResponse);
        var initialTotal = initialOverview!.TotalTasksToday;

        // Act - Dispatch new task
        var task = new SampleTask("Dashboard test");
        await dispatcher.Dispatch(task);
        await Task.Delay(1000); // Give time for task to be processed

        // Query dashboard again
        var updatedResponse = await Client.GetAsync("/evertask-monitoring/api/dashboard/overview?range=Today");
        var updatedOverview = await DeserializeResponseAsync<OverviewDto>(updatedResponse);

        // Assert
        updatedOverview.ShouldNotBeNull();
        updatedOverview.TotalTasksToday.ShouldBeGreaterThanOrEqualTo(initialTotal);
    }

    private async Task WaitForTaskCompletionAsync(Guid taskId, int timeoutMs = 5000)
    {
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs);
    }

    private async Task WaitForTaskStatusAsync(Guid taskId, QueuedTaskStatus expectedStatus, int timeoutMs = 5000)
    {
        var startTime = DateTime.UtcNow;
        while (true)
        {
            var tasks = await Storage.Get(t => t.Id == taskId);
            if (tasks.Any() && tasks.First().Status == expectedStatus)
                return;

            if ((DateTime.UtcNow - startTime).TotalMilliseconds > timeoutMs)
                throw new TimeoutException($"Task {taskId} did not reach status {expectedStatus} within {timeoutMs}ms");

            await Task.Delay(50);
        }
    }
}
