using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EverTask.Monitor.Api.DTOs.Auth;
using EverTask.Monitor.Api.DTOs.Dashboard;
using EverTask.Monitor.Api.DTOs.Queues;
using EverTask.Monitor.Api.DTOs.Statistics;
using EverTask.Monitor.Api.DTOs.Tasks;
using EverTask.Tests.Monitoring.TestHelpers;

namespace EverTask.Tests.Monitoring.Integration;

/// <summary>
/// End-to-end tests for JWT authentication flow across all monitoring API endpoints
/// </summary>
public class JwtAuthenticationE2ETests : MonitoringTestBase
{
    protected override bool RequireAuthentication => true; // Enable JWT authentication
    protected override bool EnableWorker => true; // Enable worker for complete flows

    #region Helper Methods

    private async Task<string> LoginAndGetTokenAsync()
    {
        var loginRequest = new LoginRequest("testuser", "testpass");
        var response = await Client.PostAsJsonAsync("/evertask-monitoring/api/auth/login", loginRequest);
        response.EnsureSuccessStatusCode();

        var loginResponse = await DeserializeResponseAsync<LoginResponse>(response);
        return loginResponse!.Token;
    }

    private HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string uri, string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    #endregion

    #region Complete Auth Flows

    [Fact]
    public async Task Should_complete_full_auth_flow_for_tasks_endpoint()
    {
        // Arrange - Login and get token
        var token = await LoginAndGetTokenAsync();

        // Act - Access protected tasks endpoint with token
        var request = CreateAuthenticatedRequest(HttpMethod.Get, "/evertask-monitoring/api/tasks?page=1&pageSize=10", token);
        var response = await Client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var pagedResult = await DeserializeResponseAsync<TasksPagedResponse>(response);
        pagedResult.ShouldNotBeNull();
        pagedResult.Items.ShouldNotBeNull();
        pagedResult.TotalCount.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Should_complete_full_auth_flow_for_task_detail_endpoint()
    {
        // Arrange - Login, get token, and dispatch a task
        var token = await LoginAndGetTokenAsync();

        var dispatcher = Factory.Services.GetRequiredService<ITaskDispatcher>();
        var taskId = await dispatcher.Dispatch(new TestData.SampleTask("E2E test task"));
        await Task.Delay(500); // Give time for task to be processed

        // Act - Get task detail with authentication
        var request = CreateAuthenticatedRequest(HttpMethod.Get, $"/evertask-monitoring/api/tasks/{taskId}", token);
        var response = await Client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var taskDetail = await DeserializeResponseAsync<TaskDetailDto>(response);
        taskDetail.ShouldNotBeNull();
        taskDetail.Id.ShouldBe(taskId);
        taskDetail.Type.ShouldContain("SampleTask");
    }

    [Fact]
    public async Task Should_complete_full_auth_flow_for_dashboard_overview()
    {
        // Arrange - Login and get token
        var token = await LoginAndGetTokenAsync();

        // Act - Access dashboard overview with token
        var request = CreateAuthenticatedRequest(HttpMethod.Get, "/evertask-monitoring/api/dashboard/overview?range=Today", token);
        var response = await Client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var overview = await DeserializeResponseAsync<OverviewDto>(response);
        overview.ShouldNotBeNull();
        overview.TotalTasksToday.ShouldBeGreaterThanOrEqualTo(0);
        overview.SuccessRate.ShouldBeInRange(0, 100);
    }

    [Fact]
    public async Task Should_complete_full_auth_flow_for_dashboard_recent_activity()
    {
        // Arrange - Login and get token
        var token = await LoginAndGetTokenAsync();

        // Act - Access recent activity with token
        var request = CreateAuthenticatedRequest(HttpMethod.Get, "/evertask-monitoring/api/dashboard/recent-activity?count=10", token);
        var response = await Client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var activities = await DeserializeResponseAsync<List<RecentActivityDto>>(response);
        activities.ShouldNotBeNull();
    }

    [Fact]
    public async Task Should_complete_full_auth_flow_for_queues_endpoint()
    {
        // Arrange - Login and get token
        var token = await LoginAndGetTokenAsync();

        // Act - Access queues endpoint with token
        var request = CreateAuthenticatedRequest(HttpMethod.Get, "/evertask-monitoring/api/queues", token);
        var response = await Client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var queues = await DeserializeResponseAsync<List<QueueMetricsDto>>(response);
        queues.ShouldNotBeNull();
        queues.Count.ShouldBeGreaterThan(0); // At least default queue
    }

    [Fact]
    public async Task Should_complete_full_auth_flow_for_statistics_success_rate()
    {
        // Arrange - Login and get token
        var token = await LoginAndGetTokenAsync();

        // Act - Access statistics endpoint with token
        var request = CreateAuthenticatedRequest(HttpMethod.Get, "/evertask-monitoring/api/statistics/success-rate-trend?period=Last7Days", token);
        var response = await Client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var stats = await DeserializeResponseAsync<SuccessRateTrendDto>(response);
        stats.ShouldNotBeNull();
        stats.Timestamps.ShouldNotBeNull();
        stats.SuccessRates.ShouldNotBeNull();
    }

    [Fact]
    public async Task Should_complete_full_auth_flow_for_statistics_task_types()
    {
        // Arrange - Login and get token
        var token = await LoginAndGetTokenAsync();

        // Act - Access task types statistics with token
        var request = CreateAuthenticatedRequest(HttpMethod.Get, "/evertask-monitoring/api/statistics/task-types?topN=10", token);
        var response = await Client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var taskTypes = await DeserializeResponseAsync<Dictionary<string, int>>(response);
        taskTypes.ShouldNotBeNull();
        taskTypes.Count.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Should_complete_full_auth_flow_for_statistics_execution_times()
    {
        // Arrange - Login and get token
        var token = await LoginAndGetTokenAsync();

        // Act - Access execution times statistics with token
        var request = CreateAuthenticatedRequest(HttpMethod.Get, "/evertask-monitoring/api/statistics/execution-times?days=7", token);
        var response = await Client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var execTimes = await DeserializeResponseAsync<List<ExecutionTimeDto>>(response);
        execTimes.ShouldNotBeNull();
    }

    #endregion

    #region Negative Scenarios

    [Fact]
    public async Task Should_reject_all_protected_endpoints_without_token()
    {
        // Arrange - Protected endpoints
        var protectedEndpoints = new[]
        {
            "/evertask-monitoring/api/tasks?page=1&pageSize=10",
            "/evertask-monitoring/api/dashboard/overview",
            "/evertask-monitoring/api/dashboard/recent-activity",
            "/evertask-monitoring/api/queues",
            "/evertask-monitoring/api/statistics/success-rate-trend",
            "/evertask-monitoring/api/statistics/task-types",
            "/evertask-monitoring/api/statistics/execution-times"
        };

        // Act & Assert - All should return 401
        foreach (var endpoint in protectedEndpoints)
        {
            var response = await Client.GetAsync(endpoint);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized,
                $"Endpoint {endpoint} should require authentication");
        }
    }

    [Fact]
    public async Task Should_reject_request_with_malformed_token()
    {
        // Arrange - Use a completely malformed token
        var malformedToken = "this.is.not.a.valid.jwt.token";
        var request = CreateAuthenticatedRequest(HttpMethod.Get, "/evertask-monitoring/api/tasks", malformedToken);

        // Act
        var response = await Client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_reject_request_with_invalid_token_signature()
    {
        // Arrange - Use a JWT with invalid signature (modified token)
        var token = await LoginAndGetTokenAsync();
        var invalidToken = token.Substring(0, token.Length - 10) + "INVALIDXXX";

        var request = CreateAuthenticatedRequest(HttpMethod.Get, "/evertask-monitoring/api/dashboard/overview", invalidToken);

        // Act
        var response = await Client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_reject_request_with_empty_bearer_token()
    {
        // Arrange - Empty bearer token
        var request = new HttpRequestMessage(HttpMethod.Get, "/evertask-monitoring/api/tasks");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "");

        // Act
        var response = await Client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_reject_request_with_wrong_auth_scheme()
    {
        // Arrange - Use Basic instead of Bearer
        var token = await LoginAndGetTokenAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/evertask-monitoring/api/tasks");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);

        // Act
        var response = await Client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    #endregion

    #region Public Endpoints

    [Fact]
    public async Task Should_allow_config_endpoint_without_token()
    {
        // Act - No authentication header
        var response = await Client.GetAsync("/evertask-monitoring/api/config");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var config = await DeserializeResponseAsync<ConfigResponse>(response);
        config.ShouldNotBeNull();
        config.RequireAuthentication.ShouldBeTrue();
    }

    [Fact]
    public async Task Should_allow_login_endpoint_without_token()
    {
        // Act - Login should not require existing token
        var loginRequest = new LoginRequest("testuser", "testpass");
        var response = await Client.PostAsJsonAsync("/evertask-monitoring/api/auth/login", loginRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    #endregion

    #region Token Reuse

    [Fact]
    public async Task Should_allow_token_reuse_for_multiple_requests()
    {
        // Arrange - Login once and get token
        var token = await LoginAndGetTokenAsync();

        // Act - Use same token for multiple requests
        var endpoints = new[]
        {
            "/evertask-monitoring/api/tasks?page=1&pageSize=10",
            "/evertask-monitoring/api/dashboard/overview",
            "/evertask-monitoring/api/queues"
        };

        // Assert - All requests should succeed with same token
        foreach (var endpoint in endpoints)
        {
            var request = CreateAuthenticatedRequest(HttpMethod.Get, endpoint, token);
            var response = await Client.SendAsync(request);
            response.StatusCode.ShouldBe(HttpStatusCode.OK,
                $"Token reuse should work for {endpoint}");
        }
    }

    [Fact]
    public async Task Should_return_different_tokens_for_multiple_logins()
    {
        // Arrange & Act - Login twice
        var token1 = await LoginAndGetTokenAsync();
        await Task.Delay(100); // Ensure different timestamps
        var token2 = await LoginAndGetTokenAsync();

        // Assert - Tokens should be different (different issued-at timestamps)
        token1.ShouldNotBe(token2);

        // Both tokens should work
        var request1 = CreateAuthenticatedRequest(HttpMethod.Get, "/evertask-monitoring/api/tasks", token1);
        var response1 = await Client.SendAsync(request1);
        response1.StatusCode.ShouldBe(HttpStatusCode.OK);

        var request2 = CreateAuthenticatedRequest(HttpMethod.Get, "/evertask-monitoring/api/tasks", token2);
        var response2 = await Client.SendAsync(request2);
        response2.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    #endregion
}
