using EverTask.Tests.Monitoring.TestHelpers;

namespace EverTask.Tests.Monitoring.API.Middleware;

public class BasicAuthenticationMiddlewareTests : IAsyncLifetime
{
    private MonitoringTestWebAppFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        // Create factory with authentication enabled
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
    public async Task Should_allow_anonymous_access_to_config_endpoint()
    {
        // Act - No auth header
        var response = await _client.GetAsync("/evertask/api/config");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should_require_auth_header_for_protected_endpoints()
    {
        // Act - No auth header
        var response = await _client.GetAsync("/evertask/api/dashboard/overview");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Should_return_401_with_invalid_credentials()
    {
        // Arrange
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("wronguser:wrongpass"));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        // Act
        var response = await _client.GetAsync("/evertask/api/dashboard/overview");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_return_401_without_auth_header()
    {
        // Act
        var response = await _client.GetAsync("/evertask/api/tasks");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldContain("Basic");
    }

    [Fact]
    public async Task Should_allow_authenticated_requests()
    {
        // Arrange - Add valid credentials
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("testuser:testpass"));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        // Act
        var response = await _client.GetAsync("/evertask/api/dashboard/overview");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should_allow_authenticated_requests_to_all_endpoints()
    {
        // Arrange
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("testuser:testpass"));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        // Act - Test multiple endpoints
        var endpoints = new[]
        {
            "/evertask/api/tasks?page=1&pageSize=10",
            "/evertask/api/dashboard/overview",
            "/evertask/api/dashboard/recent-activity",
            "/evertask/api/queues",
            "/evertask/api/statistics/success-rate-trend"
        };

        foreach (var endpoint in endpoints)
        {
            var response = await _client.GetAsync(endpoint);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, $"Endpoint {endpoint} should return OK");
        }
    }
}
