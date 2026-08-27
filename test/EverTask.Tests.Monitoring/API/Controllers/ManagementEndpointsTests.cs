using System.Security.Claims;
using EverTask.Monitor.Api.DTOs.Auth;
using EverTask.Monitor.Api.DTOs.Management;
using EverTask.Monitor.Api.Extensions;
using EverTask.Tests.Monitoring.TestData;
using EverTask.Tests.Monitoring.TestHelpers;

namespace EverTask.Tests.Monitoring.API.Controllers;

/// <summary>
/// The write surface of the API and the authorization it sits behind (#42).
/// </summary>
/// <remarks>
/// The whole point of the issue is that the endpoints came SECOND: the dashboard credential is a read-only
/// one shared by everyone who looks at the dashboard, and a requeue puts a handler with side effects back
/// into execution. So what is asserted here is, first, that the surface does not exist unless a host asked
/// for it, and that a read session cannot reach it — and only then that the three operations work.
/// </remarks>
public class ManagementEndpointsTests
{
    private const string ReadUser        = "testuser";
    private const string ReadPassword    = "testpass";
    private const string OperateUser     = "operator";
    private const string OperatePassword = "operate-with-a-long-secret";

    private const string BasePath = "/evertask-monitoring/api";

    [Theory]
    [InlineData("requeue")]
    [InlineData("resume")]
    [InlineData("cancel")]
    public async Task Should_answer_404_on_every_management_route_when_the_host_did_not_enable_them(string route)
    {
        // The default: a host that upgrades gains no write surface, and an operate credential it never
        // configured cannot conjure one.
        await using var factory = CreateFactory(options => options.EnableManagementEndpoints = false);
        using var client = factory.CreateClient();

        await AuthorizeAsync(client, OperateUser, OperatePassword);

        var response = await client.PostAsync(RouteFor(route, Guid.NewGuid()), null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound,
            "the prefix does not exist while EnableManagementEndpoints is false");
    }

    [Theory]
    [InlineData("requeue")]
    [InlineData("resume")]
    [InlineData("cancel")]
    public async Task Should_answer_401_on_every_management_route_when_no_session_is_presented(string route)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(RouteFor(route, Guid.NewGuid()), null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldContain("Bearer");
    }

    [Theory]
    [InlineData("requeue")]
    [InlineData("resume")]
    [InlineData("cancel")]
    public async Task Should_answer_403_on_every_management_route_when_the_session_is_read_only(string route)
    {
        // The reason the issue was closed on "no" in 4.0: this credential is the one everybody shares.
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        await AuthorizeAsync(client, ReadUser, ReadPassword);

        var response = await client.PostAsync(RouteFor(route, Guid.NewGuid()), null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden,
            "the dashboard credential is a READ credential, whatever the management endpoints are set to");
    }

    [Theory]
    [InlineData("requeue")]
    [InlineData("resume")]
    [InlineData("cancel")]
    public async Task Should_let_the_operate_role_through_on_every_management_route(string route)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        await AuthorizeAsync(client, OperateUser, OperatePassword);

        var response = await client.PostAsync(RouteFor(route, Guid.NewGuid()), null);

        // An id nobody stored: the authorization passed, and the endpoint answered about the row.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var result = await ReadResultAsync(response);
        result.Status.ShouldBe(ManagementActionStatus.NotFound);
        result.Message.ShouldNotBeNullOrEmpty("a refusal an operator cannot read is a refusal they cannot act on");
    }

    [Fact]
    public async Task Should_let_the_host_hook_authorize_a_session_the_role_would_refuse()
    {
        await using var factory = CreateFactory(options =>
            options.ManagementAuthorization = context =>
                Task.FromResult(context.Request.Headers.ContainsKey("X-Operator")));

        using var client = factory.CreateClient();

        await AuthorizeAsync(client, ReadUser, ReadPassword);
        client.DefaultRequestHeaders.Add("X-Operator", "yes");

        var response = await client.PostAsync(RouteFor("cancel", Guid.NewGuid()), null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound,
            "the host's hook decides, and it said yes for a session carrying only the read role");
    }

    [Fact]
    public async Task Should_let_the_host_hook_refuse_a_session_the_role_would_accept()
    {
        await using var factory = CreateFactory(options =>
            options.ManagementAuthorization = _ => Task.FromResult(false));

        using var client = factory.CreateClient();

        await AuthorizeAsync(client, OperateUser, OperatePassword);

        var response = await client.PostAsync(RouteFor("cancel", Guid.NewGuid()), null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden,
            "the hook REPLACES the role check, so it cannot be bypassed by holding the operate credential");
    }

    [Fact]
    public async Task Should_refuse_management_when_authentication_is_off_and_no_hook_decides()
    {
        // EnableAuthentication = false opens the READ API — it must not silently mean "anyone may cancel a
        // schedule": with no session there is no role, and only the host's hook can still say yes.
        await using var factory = CreateFactory(requireAuthentication: false);
        using var client = factory.CreateClient();

        var reads = await client.GetAsync($"{BasePath}/tasks/counts");
        reads.StatusCode.ShouldBe(HttpStatusCode.OK, "the read API is open, as it was before");

        var response = await client.PostAsync(RouteFor("cancel", Guid.NewGuid()), null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("requeue")]
    [InlineData("resume")]
    [InlineData("cancel")]
    public async Task Should_refuse_cross_site_browser_requests_on_every_management_route_when_the_host_uses_ambient_credentials(
        string route)
    {
        await using var factory = CreateAmbientCookieFactory();
        using var client = factory.CreateClient();
        using var request = AmbientCookieRequest(RouteFor(route, Guid.NewGuid()));

        request.Headers.Add("Sec-Fetch-Site", "cross-site");

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Should_refuse_a_cross_origin_management_request_when_fetch_metadata_is_absent()
    {
        await using var factory = CreateAmbientCookieFactory();
        using var client = factory.CreateClient();
        using var request = AmbientCookieRequest(RouteFor("cancel", Guid.NewGuid()));

        request.Headers.Add("Origin", "https://attacker.example");

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("same-origin")]
    [InlineData("same-site")]
    [InlineData("none")]
    public async Task Should_allow_non_browser_and_trusted_site_management_requests(string? fetchSite)
    {
        await using var factory = CreateAmbientCookieFactory();
        using var client = factory.CreateClient();
        using var request = AmbientCookieRequest(RouteFor("cancel", Guid.NewGuid()));

        if (fetchSite != null)
            request.Headers.Add("Sec-Fetch-Site", fetchSite);

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound,
            "the ambient session passed both the browser provenance check and the host hook");
    }

    [Fact]
    public async Task Should_allow_a_same_origin_management_request_when_fetch_metadata_is_absent()
    {
        await using var factory = CreateAmbientCookieFactory();
        using var client = factory.CreateClient();
        using var request = AmbientCookieRequest(RouteFor("cancel", Guid.NewGuid()));

        request.Headers.Add("Origin", "http://localhost");

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Should_leave_the_read_endpoints_where_they_were_when_management_is_enabled()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        await AuthorizeAsync(client, ReadUser, ReadPassword);

        foreach (var route in new[] { "tasks", "tasks/counts", "dashboard/overview" })
        {
            var response = await client.GetAsync($"{BasePath}/{route}");

            response.StatusCode.ShouldBe(HttpStatusCode.OK,
                $"'{route}' is a read endpoint and the read credential still opens it");
        }
    }

    [Fact]
    public async Task Should_grant_the_operate_role_to_the_management_credential_alone()
    {
        await using var factory = CreateFactory(options => options.MagicLinkToken = "a-very-long-magic-link-token");
        using var client = factory.CreateClient();

        var read = await LoginAsync(client, ReadUser, ReadPassword);
        read.CanManage.ShouldBeFalse("the dashboard credential is shared and read-only");

        var operate = await LoginAsync(client, OperateUser, OperatePassword);
        operate.CanManage.ShouldBeTrue();

        var magic = await client.PostAsJsonAsync($"{BasePath}/auth/magic",
            new MagicLinkLoginRequest("a-very-long-magic-link-token"));
        magic.StatusCode.ShouldBe(HttpStatusCode.OK);

        var magicSession = await magic.Content.ReadFromJsonAsync<LoginResponse>();
        magicSession!.CanManage.ShouldBeFalse("a magic link is a URL, and a URL gets forwarded");

        // The claim is what the gate reads, so the session has to prove it end to end, not just report it.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", magicSession.Token);
        (await client.PostAsync(RouteFor("cancel", Guid.NewGuid()), null)).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Should_report_whether_the_write_surface_exists_on_the_config_endpoint()
    {
        await using var enabled = CreateFactory();
        using var enabledClient = enabled.CreateClient();

        var on = await enabledClient.GetFromJsonAsync<JsonElement>($"{BasePath}/config");
        on.GetProperty("managementEnabled").GetBoolean().ShouldBeTrue();

        await using var disabled = CreateFactory(options => options.EnableManagementEndpoints = false);
        using var disabledClient = disabled.CreateClient();

        var off = await disabledClient.GetFromJsonAsync<JsonElement>($"{BasePath}/config");
        off.GetProperty("managementEnabled").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Should_cancel_a_schedule_through_the_management_endpoint()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var dispatcher = factory.Services.GetRequiredService<ITaskDispatcher>();
        var storage    = factory.Services.GetRequiredService<ITaskStorage>();

        var scheduleId = await dispatcher.Dispatch(new SampleRecurringTask("cancel me"),
            r => r.Schedule().Every(1).Hours(), taskKey: "management-cancel");

        await AuthorizeAsync(client, OperateUser, OperatePassword);

        var response = await client.PostAsync(RouteFor("cancel", scheduleId), null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await ReadResultAsync(response);
        result.Status.ShouldBe(ManagementActionStatus.Succeeded);
        result.TaskId.ShouldBe(scheduleId);

        var row = (await storage.Get(t => t.Id == scheduleId))[0];
        row.Status.ShouldBe(QueuedTaskStatus.Cancelled, "the operation really ran, it did not just answer 200");
    }

    [Fact]
    public async Task Should_answer_409_when_the_row_is_not_the_kind_the_operation_applies_to()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var dispatcher = factory.Services.GetRequiredService<ITaskDispatcher>();
        var taskId     = await dispatcher.Dispatch(new SampleTask("a plain one-shot"));

        await AuthorizeAsync(client, OperateUser, OperatePassword);

        var resume = await client.PostAsync(RouteFor("resume", taskId), null);
        resume.StatusCode.ShouldBe(HttpStatusCode.Conflict, "a one-shot is not a schedule");
        (await ReadResultAsync(resume)).Status.ShouldBe(ManagementActionStatus.Conflict);

        var requeue = await client.PostAsync(RouteFor("requeue", taskId), null);
        requeue.StatusCode.ShouldBe(HttpStatusCode.Conflict, "a one-shot is nobody's occurrence");
        (await ReadResultAsync(requeue)).Status.ShouldBe(ManagementActionStatus.Conflict);
    }

    [Fact]
    public async Task Should_answer_409_when_the_schedule_carries_no_task_key()
    {
        // ITaskScheduleManager addresses a schedule by the key it was dispatched under; a schedule dispatched
        // without one cannot be named at all, and the API must say so instead of failing on a null.
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var dispatcher = factory.Services.GetRequiredService<ITaskDispatcher>();
        var scheduleId = await dispatcher.Dispatch(new SampleRecurringTask("keyless"),
            r => r.Schedule().Every(2).Hours());

        await AuthorizeAsync(client, OperateUser, OperatePassword);

        var response = await client.PostAsync(RouteFor("resume", scheduleId), null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadResultAsync(response)).Message.ShouldContain("task key");
    }

    [Fact]
    public async Task Should_hold_the_gate_when_a_path_base_hides_the_prefix_from_the_middleware()
    {
        // UsePathBase moves the prefix out of Request.Path AFTER the startup filter's middleware has run, so
        // every path test there misses while routing — which sees the rewritten path — resolves the action.
        // An anonymous POST reached Cancel and ended a live schedule.
        await using var factory = CreateFactory(pathBase: "/tenant");
        using var client = factory.CreateClient();

        var dispatcher = factory.Services.GetRequiredService<ITaskDispatcher>();
        var storage    = factory.Services.GetRequiredService<ITaskStorage>();

        var scheduleId = await dispatcher.Dispatch(new SampleRecurringTask("must survive"),
            r => r.Schedule().Every(1).Hours(), taskKey: "path-base-bypass");

        var response = await client.PostAsync($"/tenant{BasePath}/management/tasks/{scheduleId}/cancel", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "no session was presented");

        var row = (await storage.Get(t => t.Id == scheduleId))[0];
        row.Status.ShouldNotBe(QueuedTaskStatus.Cancelled, "the action must never have run");
    }

    [Fact]
    public async Task Should_keep_the_write_surface_closed_behind_a_path_base_while_it_is_disabled()
    {
        await using var factory = CreateFactory(options => options.EnableManagementEndpoints = false,
            pathBase: "/tenant");
        using var client = factory.CreateClient();

        var dispatcher = factory.Services.GetRequiredService<ITaskDispatcher>();
        var storage    = factory.Services.GetRequiredService<ITaskStorage>();

        var scheduleId = await dispatcher.Dispatch(new SampleRecurringTask("must survive too"),
            r => r.Schedule().Every(1).Hours(), taskKey: "path-base-disabled");

        await AuthorizeAsync(client, OperateUser, OperatePassword);

        var response = await client.PostAsync($"/tenant{BasePath}/management/tasks/{scheduleId}/cancel", null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var row = (await storage.Get(t => t.Id == scheduleId))[0];
        row.Status.ShouldNotBe(QueuedTaskStatus.Cancelled);
    }

    [Fact]
    public async Task Should_run_the_authorization_hook_on_the_principal_the_host_authenticated()
    {
        // The documented pattern is context.User.IsInRole(...). Run before the host's UseAuthentication the
        // hook saw an anonymous principal and answered false for everyone, so the option was unusable as
        // documented.
        await using var factory = CreateFactory(
            options => options.ManagementAuthorization = context =>
                Task.FromResult(context.User.IsInRole(HostOperatorRole)),
            configurePipeline: app => app.Use(async (context, next) =>
            {
                var roles = context.Request.Headers["X-Host-Roles"].ToString();

                if (!string.IsNullOrEmpty(roles))
                {
                    var claims = roles.Split(',').Select(role => new Claim(ClaimTypes.Role, role.Trim()));
                    context.User = new ClaimsPrincipal(
                        new ClaimsIdentity(claims, "TestHostAuth", ClaimTypes.Name, ClaimTypes.Role));
                }

                await next();
            }));

        using var client = factory.CreateClient();

        // A read-only monitoring session: what decides is the host's principal, not the role on the token.
        await AuthorizeAsync(client, ReadUser, ReadPassword);

        var refused = await client.PostAsync(RouteFor("cancel", Guid.NewGuid()), null);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "the host authenticated nobody with that role");

        client.DefaultRequestHeaders.Add("X-Host-Roles", HostOperatorRole);

        var allowed = await client.PostAsync(RouteFor("cancel", Guid.NewGuid()), null);
        allowed.StatusCode.ShouldBe(HttpStatusCode.NotFound,
            "the hook read the host's principal and let the call through to the endpoint");
    }

    [Fact]
    public void Should_refuse_a_management_password_the_host_already_hands_out_for_reading()
    {
        // A username is not a secret: an equal password IS the credential, so the shared read one would be
        // promoted to operate at the first login.
        var sharedPassword = Should.Throw<InvalidOperationException>(() =>
            new ServiceCollection().AddEverTaskMonitoringApiStandalone(options =>
            {
                options.Password           = "same-secret";
                options.ManagementUsername = "operator";
                options.ManagementPassword = "same-secret";
            }));

        sharedPassword.Message.ShouldContain(nameof(EverTaskApiOptions.ManagementPassword));

        var magicLink = Should.Throw<InvalidOperationException>(() =>
            new ServiceCollection().AddEverTaskMonitoringApiStandalone(options =>
            {
                options.MagicLinkToken     = "a-very-long-magic-link-token";
                options.ManagementUsername = "operator";
                options.ManagementPassword = "a-very-long-magic-link-token";
            }));

        magicLink.Message.ShouldContain(nameof(EverTaskApiOptions.MagicLinkToken));

        var halfPair = Should.Throw<InvalidOperationException>(() =>
            new ServiceCollection().AddEverTaskMonitoringApiStandalone(options =>
                options.ManagementUsername = "operator"));

        halfPair.Message.ShouldContain(nameof(EverTaskApiOptions.ManagementPassword));

        // The distinct pair the whole model rests on stays accepted.
        Should.NotThrow(() => new ServiceCollection().AddEverTaskMonitoringApiStandalone(options =>
        {
            options.Password           = "read-secret";
            options.ManagementUsername = "operator";
            options.ManagementPassword = "operate-secret";
        }));
    }

    private const string HostOperatorRole = "evertask-operator";

    private static MonitoringTestWebAppFactory CreateAmbientCookieFactory() =>
        CreateFactory(
            options => options.ManagementAuthorization = context =>
                Task.FromResult(context.User.Identity?.IsAuthenticated == true),
            requireAuthentication: false,
            configurePipeline: app => app.Use(async (context, next) =>
            {
                if (context.Request.Headers.Cookie.ToString().Contains("session=ambient", StringComparison.Ordinal))
                {
                    context.User = new ClaimsPrincipal(
                        new ClaimsIdentity([new Claim(ClaimTypes.Name, "ambient-user")], "TestCookieAuth"));
                }

                await next();
            }));

    private static HttpRequestMessage AmbientCookieRequest(string route)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route);
        request.Headers.TryAddWithoutValidation("Cookie", "session=ambient");
        return request;
    }

    private static MonitoringTestWebAppFactory CreateFactory(Action<EverTaskApiOptions>? configure = null,
                                                             bool requireAuthentication = true,
                                                             string? pathBase = null,
                                                             Action<IApplicationBuilder>? configurePipeline = null) =>
        new(requireAuthentication, pathBase: pathBase, configurePipeline: configurePipeline,
            configureOptions: options =>
        {
            options.EnableManagementEndpoints = true;
            options.ManagementUsername        = OperateUser;
            options.ManagementPassword        = OperatePassword;
            configure?.Invoke(options);
        });

    private static string RouteFor(string route, Guid id) => $"{BasePath}/management/tasks/{id}/{route}";

    private static async Task<LoginResponse> LoginAsync(HttpClient client, string username, string password)
    {
        var response = await client.PostAsJsonAsync($"{BasePath}/auth/login", new LoginRequest(username, password));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    private static async Task AuthorizeAsync(HttpClient client, string username, string password)
    {
        var session = await LoginAsync(client, username, password);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
    }

    private static async Task<ManagementActionDto> ReadResultAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ManagementActionDto>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } }))!;
}
