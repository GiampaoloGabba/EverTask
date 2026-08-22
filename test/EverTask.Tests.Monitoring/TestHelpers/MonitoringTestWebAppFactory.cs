using EverTask.Monitor.Api.Extensions;
using EverTask.Resilience;
using EverTask.Tests.Monitoring.TestData;

namespace EverTask.Tests.Monitoring.TestHelpers;

/// <summary>
/// Custom WebApplicationFactory for testing EverTask Monitoring API
/// </summary>
public class MonitoringTestWebAppFactory(
    bool requireAuthentication = false,
    bool enableWorker = false,
    Action<IServiceCollection>? configureServices = null,
    Action<EverTaskApiOptions>? configureOptions = null,
    bool useRateLimiter = false,
    Action<IEndpointRouteBuilder>? configureEndpoints = null)
    : WebApplicationFactory<TestProgram>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Set the environment to use the bin directory as content root
        builder.UseEnvironment("Test");
        builder.UseSetting(WebHostDefaults.ContentRootKey, AppContext.BaseDirectory);

        builder.ConfigureServices(services =>
        {
            // Add EverTask with memory storage
            var everTaskBuilder = services.AddEverTask(cfg => cfg
                                                      .RegisterTasksFromAssembly(typeof(SampleTask).Assembly)
                                                      .SetChannelOptions(10)
                                                      .SetMaxDegreeOfParallelism(5)
                                                      .SetDefaultRetryPolicy(
                                                          new LinearRetryPolicy(1,
                                                              TimeSpan.FromMilliseconds(1))))
                                  .AddMemoryStorage()
                                  .AddSignalRMonitoring(); // Add SignalR monitoring for real-time events

            // Add empty queues for testing queues without tasks
            everTaskBuilder.AddQueue("reports", queueCfg => queueCfg.MaxDegreeOfParallelism       = 3);
            everTaskBuilder.AddQueue("notifications", queueCfg => queueCfg.MaxDegreeOfParallelism = 2);

            // Remove WorkerService for API tests unless explicitly enabled
            // This prevents seeded tasks from being processed and changing state during tests
            // SignalR tests need the worker enabled to execute tasks and receive events
            if (!enableWorker)
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
                // BasePath and SignalRHubPath are now fixed to "/evertask-monitoring" and "/evertask-monitoring/hub"
                options.EnableUI             = false; // Disable UI for tests
                options.EnableAuthentication = requireAuthentication;
                options.Username             = "testuser";
                options.Password             = "testpass";
                options.EnableCors           = true;

                // Allow custom options configuration (e.g., for magic link testing)
                configureOptions?.Invoke(options);
            });

            // Allow custom service configuration
            configureServices?.Invoke(services);
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

            // Endpoint-aware rate limiting (must sit between UseRouting and UseEndpoints)
            if (useRateLimiter)
            {
                app.UseRateLimiter();
            }

            // Map EverTask API (middleware is auto-registered via StartupFilter)
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapEverTaskApi();
                configureEndpoints?.Invoke(endpoints);
            });
        });
    }
}
