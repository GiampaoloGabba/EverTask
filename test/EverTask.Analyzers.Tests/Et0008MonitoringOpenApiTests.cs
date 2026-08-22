using Xunit;
using AnalyzerVerifier = EverTask.Analyzers.Tests.CSharpAnalyzerVerifier<EverTask.Analyzers.MonitoringOpenApiAnalyzer>;

namespace EverTask.Analyzers.Tests;

public class Et0008MonitoringOpenApiTests
{
    // The SDK stamps real compilations with TargetFrameworkAttribute; the test sources declare it
    // explicitly because the in-memory compilation has no SDK build step.
    private const string MonitoringStubs = """
        namespace EverTask.Monitor.Api.Options
        {
            public class EverTaskApiOptions
            {
                public bool EnableOpenApiDocument { get; set; }
            }
        }
        namespace EverTask.Monitor.Api.Scalar.Extensions
        {
            public static class ServiceCollectionExtensions
            {
                public static object AddMonitoringApiScalar(this object services) => services;
            }
        }
        """;

    private static Task VerifyAsync(string source) => AnalyzerVerifier.VerifyAsync(source, extraSource: MonitoringStubs);

    [Fact]
    public Task Enabling_the_document_on_net8_is_flagged() => VerifyAsync("""
        [assembly: System.Runtime.Versioning.TargetFramework(".NETCoreApp,Version=v8.0")]
        public static class Setup
        {
            public static void Configure(EverTask.Monitor.Api.Options.EverTaskApiOptions options)
            {
                {|ET0008:options.EnableOpenApiDocument = true|};
            }
        }
        """);

    [Fact]
    public Task Adding_scalar_on_net8_is_flagged() => VerifyAsync("""
        using EverTask.Monitor.Api.Scalar.Extensions;
        [assembly: System.Runtime.Versioning.TargetFramework(".NETCoreApp,Version=v8.0")]
        public static class Setup
        {
            public static void Configure(object services)
            {
                {|ET0008:services.AddMonitoringApiScalar()|};
            }
        }
        """);

    [Fact]
    public Task Enabling_the_document_on_net9_is_not_flagged() => VerifyAsync("""
        [assembly: System.Runtime.Versioning.TargetFramework(".NETCoreApp,Version=v9.0")]
        public static class Setup
        {
            public static void Configure(EverTask.Monitor.Api.Options.EverTaskApiOptions options)
            {
                options.EnableOpenApiDocument = true;
            }
        }
        """);

    [Fact]
    public Task Disabling_the_document_on_net8_is_not_flagged() => VerifyAsync("""
        [assembly: System.Runtime.Versioning.TargetFramework(".NETCoreApp,Version=v8.0")]
        public static class Setup
        {
            public static void Configure(EverTask.Monitor.Api.Options.EverTaskApiOptions options)
            {
                options.EnableOpenApiDocument = false;
            }
        }
        """);
}
