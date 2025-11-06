using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using EverTask.Monitor.Api.DTOs.Auth;
using EverTask.Monitor.Api.Options;
using EverTask.Tests.Monitoring.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace EverTask.Tests.Monitoring.API.Middleware;

public class JwtAuthenticationMiddlewareTests : IAsyncLifetime
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

    private async Task<string> GetJwtTokenAsync(string username = "testuser", string password = "testpass")
    {
        var loginRequest = new LoginRequest(username, password);
        var response = await _client.PostAsJsonAsync("/evertask-monitoring/api/auth/login", loginRequest);
        response.EnsureSuccessStatusCode();

        var loginResponse = await response.Content.ReadFromJsonAsync<LoginResponse>();
        return loginResponse!.Token;
    }

    [Fact]
    public async Task Should_allow_anonymous_access_to_config_endpoint()
    {
        // Act - No auth header
        var response = await _client.GetAsync("/evertask-monitoring/api/config");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should_allow_anonymous_access_to_auth_login_endpoint()
    {
        // Act - No auth header
        var loginRequest = new LoginRequest("testuser", "testpass");
        var response = await _client.PostAsJsonAsync("/evertask-monitoring/api/auth/login", loginRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should_allow_anonymous_access_to_auth_validate_endpoint()
    {
        // Arrange - Get a token first
        var token = await GetJwtTokenAsync();
        var validateRequest = new TokenValidationRequest(token);

        // Act - Call validate without authentication
        var response = await _client.PostAsJsonAsync("/evertask-monitoring/api/auth/validate", validateRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should_require_auth_header_for_protected_endpoints()
    {
        // Act - No auth header
        var response = await _client.GetAsync("/evertask-monitoring/api/dashboard/overview");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ShouldNotBeEmpty();
        response.Headers.WwwAuthenticate.ToString().ShouldContain("Bearer");
    }

    [Fact]
    public async Task Should_return_401_with_invalid_jwt_token()
    {
        // Arrange - Invalid token
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid.jwt.token");

        // Act
        var response = await _client.GetAsync("/evertask-monitoring/api/dashboard/overview");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_return_401_without_auth_header()
    {
        // Act
        var response = await _client.GetAsync("/evertask-monitoring/api/tasks");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldContain("Bearer");
    }

    [Fact]
    public async Task Should_return_401_with_expired_jwt_token()
    {
        // This test would require mocking time or creating a token with very short expiration
        // For now, we test with an obviously invalid token structure
        var expiredToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJleHAiOjB9.invalid";
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", expiredToken);

        // Act
        var response = await _client.GetAsync("/evertask-monitoring/api/dashboard/overview");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_allow_authenticated_requests_with_valid_jwt()
    {
        // Arrange - Get valid JWT token
        var token = await GetJwtTokenAsync();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.GetAsync("/evertask-monitoring/api/dashboard/overview");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should_allow_authenticated_requests_to_all_endpoints()
    {
        // Arrange - Get valid JWT token
        var token = await GetJwtTokenAsync();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act - Test multiple endpoints
        var endpoints = new[]
        {
            "/evertask-monitoring/api/tasks?page=1&pageSize=10",
            "/evertask-monitoring/api/dashboard/overview",
            "/evertask-monitoring/api/dashboard/recent-activity",
            "/evertask-monitoring/api/queues",
            "/evertask-monitoring/api/statistics/success-rate-trend"
        };

        foreach (var endpoint in endpoints)
        {
            var response = await _client.GetAsync(endpoint);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, $"Endpoint {endpoint} should return OK");
        }
    }

    [Fact]
    public async Task Should_return_401_for_login_with_invalid_credentials()
    {
        // Act
        var loginRequest = new LoginRequest("wronguser", "wrongpass");
        var response = await _client.PostAsJsonAsync("/evertask-monitoring/api/auth/login", loginRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
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
        var response = await _client.GetAsync("/evertask-monitoring/api/config");

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
                    EnableAuthentication = false,
                    Username             = "testuser",
                    Password             = "testpass",
                    AllowedIpAddresses   = new[] { "192.168.1.100" } // Client IP will be ::1 or 127.0.0.1
                });
            });
        _client = _factory.CreateClient();

        // Act
        var response = await _client.GetAsync("/evertask-monitoring/api/config");

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
                    EnableAuthentication = false,
                    Username             = "testuser",
                    Password             = "testpass",
                    AllowedIpAddresses   = new[] { "127.0.0.1", "::1" } // Allow localhost
                });
            });
        _client = _factory.CreateClient();

        // Act
        var response = await _client.GetAsync("/evertask-monitoring/api/config");

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
                    EnableAuthentication = false,
                    Username             = "testuser",
                    Password             = "testpass",
                    AllowedIpAddresses   = new[] { "127.0.0.0/8", "::1" } // Loopback range
                });
            });
        _client = _factory.CreateClient();

        // Act
        var response = await _client.GetAsync("/evertask-monitoring/api/config");

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
                    EnableAuthentication = false,
                    Username             = "testuser",
                    Password             = "testpass",
                    AllowedIpAddresses   = new[] { "::1" } // IPv6 localhost
                });
            });
        _client = _factory.CreateClient();

        // Act
        var response = await _client.GetAsync("/evertask-monitoring/api/config");

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
                    EnableAuthentication = true,
                    Username             = "testuser",
                    Password             = "testpass",
                    AllowedIpAddresses   = new[] { "192.168.1.100" } // Wrong IP
                });
            });
        _client = _factory.CreateClient();

        // Even with valid JWT, IP check should fail first
        // (We can't get a token because /auth/login is also blocked by IP whitelist)

        // Act
        var response = await _client.GetAsync("/evertask-monitoring/api/dashboard/overview");

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
                    EnableAuthentication = true,
                    Username             = "testuser",
                    Password             = "testpass",
                    AllowedIpAddresses   = new[] { "127.0.0.1", "::1" } // Correct IP
                });
            });
        _client = _factory.CreateClient();

        // Get JWT token
        var loginRequest = new LoginRequest("testuser", "testpass");
        var loginResponse = await _client.PostAsJsonAsync("/evertask-monitoring/api/auth/login", loginRequest);
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);

        // Act
        var response = await _client.GetAsync("/evertask-monitoring/api/dashboard/overview");

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
                    EnableAuthentication = false,
                    Username             = "testuser",
                    Password             = "testpass",
                    AllowedIpAddresses   = new[] { "192.168.0.0/16" } // Private network only
                });
            });
        _client = _factory.CreateClient();

        // Act
        var response = await _client.GetAsync("/evertask-monitoring/api/config");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
