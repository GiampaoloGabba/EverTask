using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EverTask.Tests.Monitoring;

/// <summary>
/// Minimal program class for WebApplicationFactory testing.
/// This provides the entry point required by WebApplicationFactory.
/// </summary>
public class TestProgram
{
    public static void Main(string[] args)
    {
        CreateHostBuilder(args).Build().Run();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.ConfigureServices(services =>
                {
                    // Empty - will be overridden by WebApplicationFactory
                });
                webBuilder.Configure(app =>
                {
                    // Empty - will be overridden by WebApplicationFactory
                });
            });
}
