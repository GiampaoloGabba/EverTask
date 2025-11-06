using EverTask.Monitor.Api.Extensions;
using EverTask.Monitor.Api.Options;
using EverTask.Tests.Monitoring.TestData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EverTask.Tests.Monitoring.TestHelpers;

/// <summary>
/// Custom WebApplicationFactory for testing EverTask Monitoring API
/// </summary>
public class MonitoringTestWebAppFactory : WebApplicationFactory<TestProgram>
{
    private readonly bool _requireAuthentication;
    private readonly bool _enableWorker;
    private readonly Action<IServiceCollection>? _configureServices;

    public MonitoringTestWebAppFactory(
        bool requireAuthentication = false,
        bool enableWorker = false,
        Action<IServiceCollection>? configureServices = null)
    {
        _requireAuthentication = requireAuthentication;
        _enableWorker          = enableWorker;
        _configureServices     = configureServices;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Set the environment to use the bin directory as content root
        builder.UseEnvironment("Test");
        builder.UseSetting(WebHostDefaults.ContentRootKey, AppContext.BaseDirectory);

        builder.ConfigureServices(services =>
        {
            // Add EverTask with memory storage
            var builder = services.AddEverTask(cfg => cfg
                                                      .RegisterTasksFromAssembly(typeof(SampleTask).Assembly)
                                                      .SetChannelOptions(10)
                                                      .SetMaxDegreeOfParallelism(5)
                                                      .SetDefaultRetryPolicy(
                                                          new EverTask.Resilience.LinearRetryPolicy(1,
                                                              TimeSpan.FromMilliseconds(1))))
                                  .AddMemoryStorage()
                                  .AddSignalRMonitoring(); // Add SignalR monitoring for real-time events

            // Add empty queues for testing queues without tasks
            builder.AddQueue("reports", queueCfg => queueCfg.MaxDegreeOfParallelism       = 3);
            builder.AddQueue("notifications", queueCfg => queueCfg.MaxDegreeOfParallelism = 2);

            // Remove WorkerService for API tests unless explicitly enabled
            // This prevents seeded tasks from being processed and changing state during tests
            // SignalR tests need the worker enabled to execute tasks and receive events
            if (!_enableWorker)
            {
                var workerServiceDescriptor = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType?.Name == "WorkerService");
                if (workerServiceDescriptor != null)
                {
                    services.Remove(workerServiceDescriptor);
                }
            }

            // Add SignalR services (required for SignalR hub)
            services.AddSignalR();

            // Add EverTask Monitoring API
            services.AddEverTaskMonitoringApiStandalone(options =>
            {
                // BasePath and SignalRHubPath are now fixed to "/monitoring" and "/monitoring/hub"
                options.EnableUI             = false; // Disable UI for tests
                options.EnableAuthentication = _requireAuthentication;
                options.Username             = "testuser";
                options.Password             = "testpass";
                options.EnableCors           = true;
            });

            // Allow custom service configuration
            _configureServices?.Invoke(services);
        });

        builder.Configure(app =>
        {
            // Seed test data
            using (var scope = app.ApplicationServices.CreateScope())
            {
                var storage = scope.ServiceProvider.GetRequiredService<ITaskStorage>();
                var seeder  = new TestDataSeeder(storage);
                seeder.SeedAsync().GetAwaiter().GetResult();
            }

            app.UseRouting();

            // Map EverTask API (middleware is auto-registered via StartupFilter)
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapEverTaskApi();
            });
        });
    }
}
