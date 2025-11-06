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
        var response = await _client.GetAsync("/monitoring/api/config");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should_require_auth_header_for_protected_endpoints()
    {
        // Act - No auth header
        var response = await _client.GetAsync("/monitoring/api/dashboard/overview");

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
        var response = await _client.GetAsync("/monitoring/api/dashboard/overview");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_return_401_without_auth_header()
    {
        // Act
        var response = await _client.GetAsync("/monitoring/api/tasks");

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
        var response = await _client.GetAsync("/monitoring/api/dashboard/overview");

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
            "/monitoring/api/tasks?page=1&pageSize=10",
            "/monitoring/api/dashboard/overview",
            "/monitoring/api/dashboard/recent-activity",
            "/monitoring/api/queues",
            "/monitoring/api/statistics/success-rate-trend"
        };

        foreach (var endpoint in endpoints)
        {
            var response = await _client.GetAsync(endpoint);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, $"Endpoint {endpoint} should return OK");
        }
    }
}

public class IpWhitelistMiddlewareTests : IAsyncLifetime
{
    private MonitoringTestWebAppFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory != null)
            await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Should_allow_all_ips_when_whitelist_is_empty()
    {
        // Arrange - Empty whitelist (default)
        _factory = new MonitoringTestWebAppFactory(requireAuthentication: false);
        _client = _factory.CreateClient();

        // Act
        var response = await _client.GetAsync("/monitoring/api/config");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should_return_403_when_ip_not_in_whitelist()
    {
        // Arrange - Configure whitelist with specific IP
        _factory = new MonitoringTestWebAppFactory(
            requireAuthentication: false,
            configureServices: services =>
            {
                // Replace EverTaskApiOptions with IP whitelist
                var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(EverTaskApiOptions));
                if (descriptor != null)
                    services.Remove(descriptor);

                services.AddSingleton(new EverTaskApiOptions
                {
                    RequireAuthentication = false,
                    Username = "testuser",
                    Password = "testpass",
                    AllowedIpAddresses = new[] { "192.168.1.100" } // Client IP will be ::1 or 127.0.0.1
                });
            });
        _client = _factory.CreateClient();

        // Act
        var response = await _client.GetAsync("/monitoring/api/config");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Should_allow_ipv4_exact_match()
    {
        // Arrange
        _factory = new MonitoringTestWebAppFactory(
            requireAuthentication: false,
            configureServices: services =>
            {
                var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(EverTaskApiOptions));
                if (descriptor != null)
                    services.Remove(descriptor);

                services.AddSingleton(new EverTaskApiOptions
                {
                    RequireAuthentication = false,
                    Username = "testuser",
                    Password = "testpass",
                    AllowedIpAddresses = new[] { "127.0.0.1", "::1" } // Allow localhost
                });
            });
        _client = _factory.CreateClient();

        // Act
        var response = await _client.GetAsync("/monitoring/api/config");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should_allow_ipv4_cidr_range()
    {
        // Arrange - 127.0.0.0/8 includes 127.0.0.1
        _factory = new MonitoringTestWebAppFactory(
            requireAuthentication: false,
            configureServices: services =>
            {
                var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(EverTaskApiOptions));
                if (descriptor != null)
                    services.Remove(descriptor);

                services.AddSingleton(new EverTaskApiOptions
                {
                    RequireAuthentication = false,
                    Username = "testuser",
                    Password = "testpass",
                    AllowedIpAddresses = new[] { "127.0.0.0/8", "::1" } // Loopback range
                });
            });
        _client = _factory.CreateClient();

        // Act
        var response = await _client.GetAsync("/monitoring/api/config");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should_allow_ipv6_exact_match()
    {
        // Arrange
        _factory = new MonitoringTestWebAppFactory(
            requireAuthentication: false,
            configureServices: services =>
            {
                var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(EverTaskApiOptions));
                if (descriptor != null)
                    services.Remove(descriptor);

                services.AddSingleton(new EverTaskApiOptions
                {
                    RequireAuthentication = false,
                    Username = "testuser",
                    Password = "testpass",
                    AllowedIpAddresses = new[] { "::1" } // IPv6 localhost
                });
            });
        _client = _factory.CreateClient();

        // Act
        var response = await _client.GetAsync("/monitoring/api/config");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should_check_ip_before_authentication()
    {
        // Arrange - IP whitelist + authentication both enabled
        _factory = new MonitoringTestWebAppFactory(
            requireAuthentication: true,
            configureServices: services =>
            {
                var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(EverTaskApiOptions));
                if (descriptor != null)
                    services.Remove(descriptor);

                services.AddSingleton(new EverTaskApiOptions
                {
                    RequireAuthentication = true,
                    Username = "testuser",
                    Password = "testpass",
                    AllowedIpAddresses = new[] { "192.168.1.100" } // Wrong IP
                });
            });
        _client = _factory.CreateClient();

        // Add valid credentials
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("testuser:testpass"));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        // Act
        var response = await _client.GetAsync("/monitoring/api/dashboard/overview");

        // Assert - Should return 403 (IP check) NOT 401 (auth check)
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Should_allow_when_both_ip_and_auth_are_valid()
    {
        // Arrange
        _factory = new MonitoringTestWebAppFactory(
            requireAuthentication: true,
            configureServices: services =>
            {
                var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(EverTaskApiOptions));
                if (descriptor != null)
                    services.Remove(descriptor);

                services.AddSingleton(new EverTaskApiOptions
                {
                    RequireAuthentication = true,
                    Username = "testuser",
                    Password = "testpass",
                    AllowedIpAddresses = new[] { "127.0.0.1", "::1" } // Correct IP
                });
            });
        _client = _factory.CreateClient();

        // Add valid credentials
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("testuser:testpass"));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        // Act
        var response = await _client.GetAsync("/monitoring/api/dashboard/overview");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should_block_cidr_range_outside_network()
    {
        // Arrange - 192.168.0.0/16 does NOT include 127.0.0.1
        _factory = new MonitoringTestWebAppFactory(
            requireAuthentication: false,
            configureServices: services =>
            {
                var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(EverTaskApiOptions));
                if (descriptor != null)
                    services.Remove(descriptor);

                services.AddSingleton(new EverTaskApiOptions
                {
                    RequireAuthentication = false,
                    Username = "testuser",
                    Password = "testpass",
                    AllowedIpAddresses = new[] { "192.168.0.0/16" } // Private network only
                });
            });
        _client = _factory.CreateClient();

        // Act
        var response = await _client.GetAsync("/monitoring/api/config");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
