using System.Net.Http.Json;
using EverTask.Monitor.Api.DTOs.Auth;
using EverTask.Tests.Monitoring.TestHelpers;

namespace EverTask.Tests.Monitoring.Integration;

public class AuthenticationIntegrationTests : IAsyncLifetime
{
    private MonitoringTestWebAppFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new MonitoringTestWebAppFactory(requireAuthentication: true);
        _client = _factory.CreateClient();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Should_protect_all_api_endpoints_except_config()
    {
        // Arrange - No auth header
        var protectedEndpoints = new[]
        {
            "/evertask-monitoring/api/tasks",
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
            var response = await _client.GetAsync(endpoint);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, $"Endpoint {endpoint} should require authentication");
        }
    }

    [Fact]
    public async Task Should_allow_access_with_valid_credentials()
    {
        // Arrange - First login to get JWT token
        var loginRequest = new { username = "testuser", password = "testpass" };
        var loginResponse = await _client.PostAsJsonAsync("/evertask-monitoring/api/auth/login", loginRequest);
        loginResponse.EnsureSuccessStatusCode();

        var loginResult = await DeserializeResponseAsync<LoginResponse>(loginResponse);
        var token = loginResult!.Token;

        // Set JWT token in Authorization header
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var endpoints = new[]
        {
            "/evertask-monitoring/api/tasks?page=1&pageSize=10",
            "/evertask-monitoring/api/dashboard/overview",
            "/evertask-monitoring/api/queues",
            "/evertask-monitoring/api/config"
        };

        // Act & Assert
        foreach (var endpoint in endpoints)
        {
            var response = await _client.GetAsync(endpoint);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, $"Endpoint {endpoint} should be accessible with valid credentials");
        }
    }

    [Fact]
    public async Task Should_reject_invalid_credentials()
    {
        // Arrange - Use an invalid/malformed JWT token
        var invalidToken = "invalid.jwt.token.here";
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", invalidToken);

        // Act
        var response = await _client.GetAsync("/evertask-monitoring/api/dashboard/overview");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_reject_malformed_auth_header()
    {
        // Arrange
        _client.DefaultRequestHeaders.Add("Authorization", "Invalid Header Format");

        // Act
        var response = await _client.GetAsync("/evertask-monitoring/api/dashboard/overview");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_include_www_authenticate_header_in_401_response()
    {
        // Act
        var response = await _client.GetAsync("/evertask-monitoring/api/tasks");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ShouldNotBeEmpty();
        response.Headers.WwwAuthenticate.ToString().ShouldContain("Bearer");
    }

    [Fact]
    public async Task Should_allow_config_endpoint_without_authentication()
    {
        // Act - No auth header
        var response = await _client.GetAsync("/evertask-monitoring/api/config");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var config = await DeserializeResponseAsync<ConfigResponse>(response);
        config.ShouldNotBeNull();
        config.RequireAuthentication.ShouldBe(true);
    }

    private async Task<T?> DeserializeResponseAsync<T>(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(content, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }
}

public record ConfigResponse(
    string ApiBasePath,
    string UiBasePath,
    string SignalRHubPath,
    bool RequireAuthentication,
    bool UiEnabled
);
