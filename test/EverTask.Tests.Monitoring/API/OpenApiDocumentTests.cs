#if NET9_0_OR_GREATER
using EverTask.Monitor.Api.Scalar.Extensions;
#endif
using EverTask.Tests.Monitoring.TestHelpers;

namespace EverTask.Tests.Monitoring.API;

/// <summary>
/// The isolated OpenAPI document + Scalar UI (net9+) and the route-prefix scoping: the monitoring
/// endpoints live under /evertask-monitoring while host controllers keep their natural routes and
/// stay out of the monitoring document.
/// </summary>
public class OpenApiDocumentTests
{
#if NET9_0_OR_GREATER
    private const string DocumentPath = "/evertask-monitoring/openapi/evertask-monitoring.json";
#endif

    [Fact]
    public async Task Should_keep_host_controllers_unprefixed()
    {
        await using var factory = new MonitoringTestWebAppFactory();
        using var client        = factory.CreateClient();

        var natural = await client.GetAsync("/HostSample/ping");
        natural.StatusCode.ShouldBe(HttpStatusCode.OK);

        var prefixed = await client.GetAsync("/evertask-monitoring/HostSample/ping");
        prefixed.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

#if NET9_0_OR_GREATER
    [Fact]
    public async Task Should_serve_openapi_document_when_enabled()
    {
        await using var factory = new MonitoringTestWebAppFactory(
            configureOptions: options => options.EnableOpenApiDocument = true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(DocumentPath);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();

        paths.ShouldNotBeEmpty();
        paths.ShouldAllBe(p => p.StartsWith("/evertask-monitoring/"));
        paths.ShouldNotContain("/HostSample/ping");
    }

    [Fact]
    public async Task Should_not_map_openapi_document_by_default()
    {
        await using var factory = new MonitoringTestWebAppFactory();
        using var client        = factory.CreateClient();

        var response = await client.GetAsync(DocumentPath);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Should_serve_scalar_ui_and_enable_document_when_scalar_added()
    {
        await using var factory = new MonitoringTestWebAppFactory(
            configureServices: services => services.AddMonitoringApiScalar());
        using var client = factory.CreateClient();

        var scalar = await client.GetAsync("/evertask-monitoring/scalar");
        scalar.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await scalar.Content.ReadAsStringAsync()).ShouldContain("scalar");

        // Adding the Scalar package enables the document without touching EnableOpenApiDocument
        var doc = await client.GetAsync(DocumentPath);
        doc.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
#endif
}
