using EverTask.Tests.Monitoring.TestHelpers;
using Microsoft.AspNetCore.HttpOverrides;

namespace EverTask.Tests.Monitoring.API.Middleware;

/// <summary>
/// Where the IP whitelist takes the client address from (#47).
/// </summary>
/// <remarks>
/// It used to read <c>X-Forwarded-For</c> and believe it, which any direct caller can set: the whitelist was
/// advisory, not a boundary. The address is now <c>Connection.RemoteIpAddress</c> and nothing else, and a
/// host behind a reverse proxy makes the header true the way ASP.NET Core intends — <c>UseForwardedHeaders</c>
/// with its <c>KnownProxies</c>, which rewrites that address before the request is routed.
/// </remarks>
public class IpWhitelistTrustTests
{
    private const string Monitoring = "/evertask-monitoring";
    private const string AllowedIp  = "10.0.0.1";

    /// <summary>The address of the only peer a proxied host is configured to believe.</summary>
    private const string ProxyIp = "192.0.2.10";

    /// <summary>A caller that is neither whitelisted nor a known proxy.</summary>
    private const string StrangerIp = "203.0.113.9";

    [Fact]
    public async Task Should_refuse_a_spoofed_forwarded_header_carrying_a_whitelisted_address()
    {
        // The bypass: send the whitelist's own address in a header nobody vouched for.
        await using var factory = HostSeenFrom(StrangerIp);
        using var client = factory.CreateClient();

        client.DefaultRequestHeaders.Add("X-Forwarded-For", AllowedIp);

        var response = await client.GetAsync($"{Monitoring}/api/tasks");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden,
            "a header the caller writes cannot be what the whitelist believes");

        var body = await response.Content.ReadAsStringAsync();
        body.Contains("\"items\"", StringComparison.Ordinal)
            .ShouldBeFalse("nothing of the task list may reach a caller outside the whitelist");
    }

    [Fact]
    public async Task Should_refuse_a_spoofed_forwarded_header_on_the_dashboard_too()
    {
        // The dashboard files are behind the whitelist alone: no JWT would have caught this one.
        await using var factory = HostSeenFrom(StrangerIp, ui: true);
        using var client = factory.CreateClient();

        client.DefaultRequestHeaders.Add("X-Forwarded-For", AllowedIp);

        (await client.GetAsync(Monitoring)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Should_whitelist_through_forwarded_headers_when_the_host_configures_the_middleware()
    {
        // The documented road for a host behind a proxy: the framework decides whether the header may be
        // believed, from the peer that actually connected.
        await using var factory = ProxiedHost();
        using var client = factory.CreateClient();

        client.DefaultRequestHeaders.Add("X-Forwarded-For", AllowedIp);

        var response = await client.GetAsync($"{Monitoring}/api/tasks");

        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            "the proxy is known, so the address it forwards is the client's");
    }

    [Fact]
    public async Task Should_still_refuse_a_stranger_forwarded_by_a_known_proxy()
    {
        // The proxy being trusted does not make its traffic trusted: what it forwards still has to be on the
        // list.
        await using var factory = ProxiedHost();
        using var client = factory.CreateClient();

        client.DefaultRequestHeaders.Add("X-Forwarded-For", StrangerIp);

        (await client.GetAsync($"{Monitoring}/api/tasks")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Should_keep_admitting_the_connection_address_itself()
    {
        // The plain case, with no proxy and no header at all.
        await using var factory = HostSeenFrom(AllowedIp);
        using var client = factory.CreateClient();

        (await client.GetAsync($"{Monitoring}/api/tasks")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("10.0.0.0/-1", "10.0.0.7")]
    [InlineData("10.0.0.0/33", "10.0.0.7")]
    [InlineData("2001:db8::/-1", "2001:db8::7")]
    [InlineData("2001:db8::/129", "2001:db8::7")]
    public async Task Should_refuse_out_of_range_cidr_prefixes_instead_of_treating_them_as_matches(
        string cidr, string clientIp)
    {
        await using var factory = HostSeenFrom(clientIp, allowedEntries: [cidr]);
        using var client = factory.CreateClient();

        (await client.GetAsync($"{Monitoring}/api/tasks")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Should_match_an_ipv4_mapped_ipv6_client_to_an_exact_ipv4_entry()
    {
        await using var factory = HostSeenFrom("::ffff:203.0.113.7", allowedEntries: ["203.0.113.7"]);
        using var client = factory.CreateClient();

        (await client.GetAsync($"{Monitoring}/api/tasks")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should_match_an_ipv4_mapped_ipv6_client_to_an_ipv4_cidr_entry()
    {
        await using var factory = HostSeenFrom("::ffff:203.0.113.7", allowedEntries: ["203.0.113.0/24"]);
        using var client = factory.CreateClient();

        (await client.GetAsync($"{Monitoring}/api/tasks")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>A host whose whitelist holds <see cref="AllowedIp"/>, with the connection coming from <paramref name="clientIp"/>.</summary>
    private static MonitoringTestWebAppFactory HostSeenFrom(string clientIp, bool ui = false,
                                                            string[]? allowedEntries = null) =>
        new(configureOptions: options =>
            {
                options.AllowedIpAddresses = allowedEntries ?? [AllowedIp];
                options.EnableUI           = ui;
            },
            configurePipeline: app => app.Use((context, next) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(clientIp);
                return next(context);
            }));

    /// <summary>
    /// The same host behind a reverse proxy, wired the way the docs tell a host to wire one: the connection
    /// comes from the proxy, and <c>UseForwardedHeaders</c> is told that this peer may be believed.
    /// </summary>
    private static MonitoringTestWebAppFactory ProxiedHost() =>
        new(configureOptions: options => options.AllowedIpAddresses = [AllowedIp],
            configurePipeline: app =>
            {
                app.Use((context, next) =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(ProxyIp);
                    return next(context);
                });

                app.UseForwardedHeaders(new ForwardedHeadersOptions
                {
                    ForwardedHeaders = ForwardedHeaders.XForwardedFor,
                    KnownProxies     = { IPAddress.Parse(ProxyIp) }
                });
            });
}
