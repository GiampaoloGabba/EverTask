using EverTask.Tests.Monitoring.TestHelpers;

namespace EverTask.Tests.Monitoring.API.Middleware;

/// <summary>
/// The monitoring protections under a host that calls <c>app.UsePathBase(...)</c> (#46).
/// </summary>
/// <remarks>
/// <c>JwtAuthenticationMiddleware</c> runs outside routing and reads <c>Request.Path</c> before the path base
/// has been taken out of it, so on such a host every path test in it missed and ALL its layers were skipped
/// at once: the IP whitelist, the JWT on every read endpoint, and the SignalR handshake. Each test here is
/// paired with the same call without a path base, which has always been protected — the pair is what says
/// the protection follows the surface instead of following the prefix.
/// </remarks>
public class PathBaseProtectionTests
{
    private const string PathBase   = "/tenant";
    private const string Monitoring = "/evertask-monitoring";

    /// <summary>An address that is never the caller's, so a whitelist holding it blocks everyone.</summary>
    private const string AllowedIp = "10.0.0.1";

    [Fact]
    public async Task Should_require_a_session_on_the_read_endpoints_under_a_path_base()
    {
        // The payload of these endpoints carries serialized requests and exception messages.
        await using var factory = new MonitoringTestWebAppFactory(requireAuthentication: true, pathBase: PathBase);
        using var client = factory.CreateClient();

        var underPathBase = await client.GetAsync($"{PathBase}{Monitoring}/api/tasks");
        underPathBase.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the read surface is not anonymous");

        var body = await underPathBase.Content.ReadAsStringAsync();
        body.Contains("\"items\"", StringComparison.Ordinal)
            .ShouldBeFalse("nothing of the task list may reach an unauthenticated caller");
    }

    [Fact]
    public async Task Should_require_a_session_on_the_read_endpoints_with_and_without_a_path_base_alike()
    {
        await using var withBase = new MonitoringTestWebAppFactory(requireAuthentication: true, pathBase: PathBase);
        await using var without  = new MonitoringTestWebAppFactory(requireAuthentication: true);

        using var withBaseClient = withBase.CreateClient();
        using var plainClient    = without.CreateClient();

        foreach (var (client, url) in new[]
                 {
                     (withBaseClient, $"{PathBase}{Monitoring}/api/tasks"),
                     (plainClient, $"{Monitoring}/api/tasks")
                 })
        {
            var anonymous = await client.GetAsync(url);
            anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, $"{url} is protected");
            anonymous.Headers.WwwAuthenticate.ToString().ShouldContain("Bearer");
        }
    }

    [Fact]
    public async Task Should_still_answer_the_anonymous_endpoints_under_a_path_base()
    {
        // The config and login endpoints have to answer before a caller can hold a token at all: protecting
        // the surface must not close the door that lets a client in.
        await using var factory = new MonitoringTestWebAppFactory(requireAuthentication: true, pathBase: PathBase);
        using var client = factory.CreateClient();

        var config = await client.GetAsync($"{PathBase}{Monitoring}/api/config");
        config.StatusCode.ShouldBe(HttpStatusCode.OK);

        var login = await client.PostAsJsonAsync($"{PathBase}{Monitoring}/api/auth/login",
            new { username = "testuser", password = "testpass" });
        login.StatusCode.ShouldBe(HttpStatusCode.OK);

        // And the session it hands out really opens the read surface behind the path base.
        var session = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", session.GetProperty("token").GetString());

        var tasks = await client.GetAsync($"{PathBase}{Monitoring}/api/tasks");
        tasks.StatusCode.ShouldBe(HttpStatusCode.OK, "an authenticated caller still gets through");
    }

    [Fact]
    public async Task Should_apply_the_ip_whitelist_under_a_path_base_on_the_api_and_on_the_dashboard()
    {
        // The whitelist is the layer that covers the dashboard files too, which no JWT protects.
        await using var factory = new MonitoringTestWebAppFactory(pathBase: PathBase, configureOptions: options =>
        {
            options.AllowedIpAddresses = [AllowedIp];
            options.EnableUI           = true;
        });

        using var blocked = factory.CreateClient();
        blocked.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.9");

        foreach (var url in new[] { $"{PathBase}{Monitoring}/api/tasks", $"{PathBase}{Monitoring}" })
        {
            var response = await blocked.GetAsync(url);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{url} is behind the whitelist");
        }

        using var allowed = factory.CreateClient();
        allowed.DefaultRequestHeaders.Add("X-Forwarded-For", AllowedIp);

        var fromWhitelist = await allowed.GetAsync($"{PathBase}{Monitoring}/api/tasks");
        fromWhitelist.StatusCode.ShouldBe(HttpStatusCode.OK, "the whitelisted address still gets in");
    }

    [Fact]
    public async Task Should_refuse_an_anonymous_hub_handshake_under_a_path_base()
    {
        // The hub is not MVC, so no filter can cover it: an anonymous negotiate used to be answered with a
        // connection id, which is the whole handshake.
        await using var factory = new MonitoringTestWebAppFactory(requireAuthentication: true, pathBase: PathBase);
        using var client = factory.CreateClient();

        var negotiate = await client.PostAsync($"{PathBase}{Monitoring}/hub/negotiate?negotiateVersion=1", null);

        negotiate.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var body = await negotiate.Content.ReadAsStringAsync();
        body.Contains("connectionId", StringComparison.Ordinal)
            .ShouldBeFalse("an anonymous caller must not be handed a connection");
    }

    [Fact]
    public async Task Should_let_an_authenticated_hub_handshake_through_under_a_path_base()
    {
        await using var factory = new MonitoringTestWebAppFactory(requireAuthentication: true, pathBase: PathBase);
        using var client = factory.CreateClient();

        var login = await client.PostAsJsonAsync($"{PathBase}{Monitoring}/api/auth/login",
            new { username = "testuser", password = "testpass" });

        var session = await login.Content.ReadFromJsonAsync<JsonElement>();
        var token   = session.GetProperty("token").GetString();

        // The handshake carries its token in the query string: a WebSocket upgrade cannot set headers.
        var negotiate = await client.PostAsync(
            $"{PathBase}{Monitoring}/hub/negotiate?negotiateVersion=1&access_token={token}", null);

        negotiate.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await negotiate.Content.ReadAsStringAsync()).ShouldContain("connectionId");
    }

    [Fact]
    public async Task Should_leave_the_host_own_endpoints_alone_under_a_path_base()
    {
        // The guard is attached by ROUTE, so a host endpoint that happens to share the pipeline keeps
        // answering anonymously — the monitoring policy is not the host's policy.
        await using var factory = new MonitoringTestWebAppFactory(requireAuthentication: true, pathBase: PathBase,
            configureOptions: options => options.AllowedIpAddresses = [AllowedIp]);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.9");

        var host = await client.GetAsync($"{PathBase}/HostSample/ping");

        host.StatusCode.ShouldBe(HttpStatusCode.OK,
            "neither the whitelist nor the JWT may reach an endpoint that is not ours");
    }
}
