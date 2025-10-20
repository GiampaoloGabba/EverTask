using EverTask.Monitor.Api.DTOs.Tasks;
using EverTask.Tests.Monitoring.TestHelpers;

namespace EverTask.Tests.Monitoring.API.Controllers;

public class TasksControllerTests : MonitoringTestBase
{
    [Fact]
    public async Task Should_get_tasks_with_pagination()
    {
        // Arrange
        const int page = 1;
        const int pageSize = 10;

        // Act
        var response = await Client.GetAsync($"/evertask/api/tasks?page={page}&pageSize={pageSize}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var pagedResponse = await DeserializeResponseAsync<TasksPagedResponse>(response);
        pagedResponse.ShouldNotBeNull();
        pagedResponse.Items.Count.ShouldBeLessThanOrEqualTo(pageSize);
        pagedResponse.Page.ShouldBe(page);
        pagedResponse.PageSize.ShouldBe(pageSize);
        pagedResponse.TotalCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Should_get_tasks_with_filters()
    {
        // Arrange - Filter by completed status
        const string status = "Completed";

        // Act - Note: Use 'statuses' (plural) to match TaskFilter.Statuses property
        var response = await Client.GetAsync($"/evertask/api/tasks?statuses={status}&page=1&pageSize=10");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var pagedResponse = await DeserializeResponseAsync<TasksPagedResponse>(response);
        pagedResponse.ShouldNotBeNull();
        pagedResponse.Items.Count.ShouldBeGreaterThan(0, "Expected to find completed tasks");

        foreach (var task in pagedResponse.Items)
        {
            task.Status.ShouldBe(QueuedTaskStatus.Completed);
        }
    }

    [Fact]
    public async Task Should_get_task_detail_when_exists()
    {
        // Arrange - Get a task ID from the list
        var listResponse = await Client.GetAsync("/evertask/api/tasks?page=1&pageSize=1");
        var pagedResponse = await DeserializeResponseAsync<TasksPagedResponse>(listResponse);
        var taskId = pagedResponse!.Items.First().Id;

        // Act
        var response = await Client.GetAsync($"/evertask/api/tasks/{taskId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var taskDetail = await DeserializeResponseAsync<TaskDetailDto>(response);
        taskDetail.ShouldNotBeNull();
        taskDetail.Id.ShouldBe(taskId);
        taskDetail.Type.ShouldNotBeNullOrEmpty();
        taskDetail.Handler.ShouldNotBeNullOrEmpty();
        taskDetail.Status.ShouldBe(QueuedTaskStatus.Completed);
    }

    [Fact]
    public async Task Should_return_404_when_task_not_found()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var response = await Client.GetAsync($"/evertask/api/tasks/{nonExistentId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Should_get_status_audit_history()
    {
        // Arrange - Get a completed task ID
        var listResponse = await Client.GetAsync("/evertask/api/tasks?status=Completed&page=1&pageSize=1");
        var pagedResponse = await DeserializeResponseAsync<TasksPagedResponse>(listResponse);
        var taskId = pagedResponse!.Items.First().Id;

        // Act
        var response = await Client.GetAsync($"/evertask/api/tasks/{taskId}/status-audit");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var statusAudits = await DeserializeResponseAsync<List<StatusAuditDto>>(response);
        statusAudits.ShouldNotBeNull();
        statusAudits.Count.ShouldBeGreaterThan(0);

        // Verify audit trail contains expected statuses
        statusAudits.Any(a => a.NewStatus == QueuedTaskStatus.Queued).ShouldBeTrue();
    }

    [Fact]
    public async Task Should_get_runs_audit_history()
    {
        // Arrange - Get a completed task ID
        var listResponse = await Client.GetAsync("/evertask/api/tasks?status=Completed&page=1&pageSize=1");
        var pagedResponse = await DeserializeResponseAsync<TasksPagedResponse>(listResponse);
        var taskId = pagedResponse!.Items.First().Id;

        // Act
        var response = await Client.GetAsync($"/evertask/api/tasks/{taskId}/runs-audit");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var runsAudits = await DeserializeResponseAsync<List<RunsAuditDto>>(response);
        runsAudits.ShouldNotBeNull();
        // Runs audit might be empty for non-recurring tasks, so we just verify the endpoint works
    }
}
