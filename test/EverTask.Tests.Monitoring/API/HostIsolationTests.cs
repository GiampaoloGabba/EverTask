using EverTask.Tests.Monitoring.TestHelpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace EverTask.Tests.Monitoring.API;

/// <summary>
/// Issue #21: AddMonitoringApi()/MapEverTaskApi() must be additive. The host keeps its MVC JSON
/// defaults, its fallback route and its CORS-free endpoints; the monitoring JSON contract, the
/// SPA fallback, the CORS policy and the login rate limit apply only under /evertask-monitoring.
/// </summary>
public class HostIsolationTests
{
    [Fact]
    public async Task Should_not_touch_host_json_options()
    {
        await using var factory = new MonitoringTestWebAppFactory();
        using var client        = factory.CreateClient();

        // Host controller keeps MVC defaults: numeric enums, nulls written
        var host = await (await client.GetAsync("/HostSample/json-contract")).Content.ReadAsStringAsync();
        host.ShouldContain("\"day\":5");
        host.ShouldContain("\"missing\":null");

        // Monitoring endpoints keep their own contract: string enums
        var monitoring = await (await client.GetAsync("/evertask-monitoring/api/tasks")).Content.ReadAsStringAsync();
        monitoring.ShouldContain("\"status\":\"");
    }

    [Fact]
    public async Task Should_not_hijack_host_fallback()
    {
        await using var factory = new MonitoringTestWebAppFactory(
            configureOptions: options => options.EnableUI = true,
            configureEndpoints: endpoints => endpoints.MapFallback(async context =>
            {
                context.Response.StatusCode = 200;
                await context.Response.WriteAsync("host-fallback");
            }));
        using var client = factory.CreateClient();

        // Outside the monitoring base path the host's own fallback wins (no AmbiguousMatchException)
        var host = await client.GetAsync("/some/unmatched/host/path");
        host.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.Content.ReadAsStringAsync()).ShouldBe("host-fallback");

        // Under the base path the monitoring SPA fallback serves the dashboard
        var spa = await client.GetAsync("/evertask-monitoring/tasks");
        spa.StatusCode.ShouldBe(HttpStatusCode.OK);
        spa.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
    }

    [Fact]
    public async Task Should_rate_limit_login_with_namespaced_policy()
    {
        await using var factory = new MonitoringTestWebAppFactory(
            requireAuthentication: true,
            useRateLimiter: true);
        using var client = factory.CreateClient();

        var payload = new StringContent(
            """{"username":"testuser","password":"wrong"}""", Encoding.UTF8, "application/json");

        // 5 attempts per window pass through (and fail authentication)...
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var response = await client.PostAsync("/evertask-monitoring/api/auth/login", payload);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, $"attempt {attempt}");
        }

        // ...the 6th is throttled by the evertask-monitoring-login policy
        var throttled = await client.PostAsync("/evertask-monitoring/api/auth/login", payload);
        throttled.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Should_apply_cors_only_under_base_path()
    {
        await using var factory = new MonitoringTestWebAppFactory();
        using var client        = factory.CreateClient();

        using var monitoringRequest = new HttpRequestMessage(HttpMethod.Get, "/evertask-monitoring/api/config");
        monitoringRequest.Headers.Add("Origin", "http://example.com");
        var monitoring = await client.SendAsync(monitoringRequest);
        monitoring.Headers.Contains("Access-Control-Allow-Origin").ShouldBeTrue();

        using var hostRequest = new HttpRequestMessage(HttpMethod.Get, "/HostSample/ping");
        hostRequest.Headers.Add("Origin", "http://example.com");
        var host = await client.SendAsync(hostRequest);
        host.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }
}
