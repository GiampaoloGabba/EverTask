using EverTask.Abstractions;
using EverTask.Example.AspnetCore;
using EverTask.Logging.Serilog;
using EverTask.Monitor.Api.Extensions;
using EverTask.Monitor.AspnetCore.SignalR;
using Serilog;
using Serilog.Settings.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.EnableAnnotations();
    c.SwaggerDoc("v1", new() { Title = "My Application API", Version = "v1" });
});

builder.Services.AddSignalR();

// Configure EverTask with multiple queues for workload isolation
builder.Services.AddEverTask(opt =>
       {
           opt.SetThrowIfUnableToPersist(true)
              .RegisterTasksFromAssembly(typeof(Program).Assembly)
              // Enable log capture for debugging and auditing
              .WithPersistentLogger(log => log
                  .SetMinimumLevel(LogLevel.Information)
                  .SetMaxLogsPerTask(1000));
       })
       // Configure the default queue for general tasks
       .ConfigureDefaultQueue(q => q
           .SetMaxDegreeOfParallelism(5)
           .SetChannelCapacity(100)
           .SetFullBehavior(EverTask.Configuration.QueueFullBehavior.FallbackToDefault))
       // Add a high-priority queue for critical tasks like payments
       .AddQueue("high-priority", q => q
           .SetMaxDegreeOfParallelism(10)
           .SetChannelCapacity(200)
           .SetFullBehavior(EverTask.Configuration.QueueFullBehavior.Wait)
           .SetDefaultTimeout(TimeSpan.FromMinutes(5)))
       // Add a background queue for low-priority CPU-intensive tasks
       .AddQueue("low-priority", q => q
           .SetMaxDegreeOfParallelism(2)  // Limit parallelism for CPU-intensive work
           .SetChannelCapacity(50)
           .SetFullBehavior(EverTask.Configuration.QueueFullBehavior.FallbackToDefault)
           .SetDefaultTimeout(TimeSpan.FromMinutes(30)))
       // Configure the recurring queue for scheduled tasks
       .ConfigureRecurringQueue(q => q
           .SetMaxDegreeOfParallelism(3)
           .SetChannelCapacity(75))
       .AddMemoryStorage()
       .AddSignalRMonitoring()
       .AddSerilog(opt => opt.ReadFrom.Configuration(builder.Configuration, new ConfigurationReaderOptions { SectionName = "EverTaskSerilog" }))
       .AddMonitoringApi(options =>
       {
           options.EnableUI             = true;
           options.EnableSwagger        = true; // Enable separate Swagger document for monitoring API
           options.Username             = "admin";
           options.Password             = "admin";
           options.EnableAuthentication = true; // Disable auth for demo
       });

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "My Application API");
        c.SwaggerEndpoint("/swagger/evertask-monitoring/swagger.json", "EverTask Monitoring API");
    });
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.MapEverTaskMonitorHub();

app.MapEverTaskApi();

// Dispatch sample tasks to demonstrate execution logs feature
await DispatchSampleTasksAsync(app.Services);

app.Run();

static async Task DispatchSampleTasksAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var dispatcher = scope.ServiceProvider.GetRequiredService<ITaskDispatcher>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    logger.LogInformation("=== Dispatching sample tasks for monitoring dashboard demo ===");

    // 1. Quick tasks - immediate execution with minimal logging (default queue)
    await dispatcher.Dispatch(new QuickTask("Send Welcome Email", 300));
    await dispatcher.Dispatch(new QuickTask("Update User Profile", 200));
    await dispatcher.Dispatch(new QuickTask("Generate Report", 800));

    // 2. High-priority tasks - critical operations routed to dedicated high-priority queue
    await dispatcher.Dispatch(new HighPriorityTask("Process Payment", "ORD-12345", 400));
    await dispatcher.Dispatch(new HighPriorityTask("Confirm Order", "ORD-12346", 300));
    await dispatcher.Dispatch(new HighPriorityTask("Send Order Notification", "ORD-12347", 200));

    // 3. Low-priority tasks - background jobs with limited parallelism
    await dispatcher.Dispatch(new LowPriorityTask("Cleanup Old Logs", ItemCount: 10, ProcessingTimePerItemMs: 50));
    await dispatcher.Dispatch(new LowPriorityTask("Generate Monthly Report", ItemCount: 20, ProcessingTimePerItemMs: 100));
    await dispatcher.Dispatch(new LowPriorityTask("Archive Historical Data", ItemCount: 15, ProcessingTimePerItemMs: 80));

    // 4. Demo logging tasks - rich logging at multiple levels (default queue)
    await dispatcher.Dispatch(new DemoLoggingTask("Data Processing Job", LogCount: 15, ShouldFail: false));
    await dispatcher.Dispatch(new DemoLoggingTask("Image Processing", LogCount: 30, ShouldFail: false));

    // 5. Delayed tasks - demonstrate scheduled execution
    await dispatcher.Dispatch(new DemoLoggingTask("Scheduled Analytics", LogCount: 20, ShouldFail: false),
        options => options.RunDelayed(TimeSpan.FromSeconds(10)));

    await dispatcher.Dispatch(new QuickTask("Delayed Notification", 500),
        options => options.RunDelayed(TimeSpan.FromSeconds(15)));

    await dispatcher.Dispatch(new HighPriorityTask("Delayed Payment Retry", "ORD-12348", 500),
        options => options.RunDelayed(TimeSpan.FromSeconds(5)));

    // 6. Tasks that will fail - demonstrate error logging
    await dispatcher.Dispatch(new DemoLoggingTask("Failed Import Job", LogCount: 10, ShouldFail: true));
    await dispatcher.Dispatch(new DemoLoggingTask("Failed Validation", LogCount: 5, ShouldFail: true));

    // 7. Heavy logging tasks - for pagination testing (150+ logs total)
    await dispatcher.Dispatch(new DemoLoggingTask("Heavy Processing Task 1", LogCount: 50, ShouldFail: false));
    await dispatcher.Dispatch(new DemoLoggingTask("Heavy Processing Task 2", LogCount: 60, ShouldFail: false));
    await dispatcher.Dispatch(new DemoLoggingTask("Heavy Processing Task 3", LogCount: 70, ShouldFail: false));

    // 8. Mixed batch - various scenarios across different queues
    await dispatcher.Dispatch(new QuickTask("Quick Backup", 400));
    await dispatcher.Dispatch(new DemoLoggingTask("Medium Task", LogCount: 25, ShouldFail: false));
    await dispatcher.Dispatch(new QuickTask("Quick Cleanup", 150));
    await dispatcher.Dispatch(new HighPriorityTask("Emergency Transaction", "TRX-99999", 600));
    await dispatcher.Dispatch(new LowPriorityTask("Optimize Database Indexes", ItemCount: 8, ProcessingTimePerItemMs: 120));

    // 9. Recurring tasks - demonstrate recurring execution patterns
    await dispatcher.Dispatch(
        new DemoLoggingTask("Recurring Every Minute", LogCount: 5, ShouldFail: false),
        taskBuilder => taskBuilder.Schedule().EveryMinute().MaxRuns(5)
    );

    await dispatcher.Dispatch(
        new QuickTask("Recurring Every 30 Seconds", 200),
        taskBuilder => taskBuilder.Schedule().UseCron("*/30 * * * * *").MaxRuns(10)
    );

    await dispatcher.Dispatch(
        new DemoLoggingTask("Daily Recurring Task", LogCount: 3, ShouldFail: false),
        taskBuilder => taskBuilder.Schedule().EveryDay().MaxRuns(3)
    );

    // 10. Recurring high-priority task - health checks
    await dispatcher.Dispatch(
        new HighPriorityTask("Health Check", "SYSTEM", 100),
        taskBuilder => taskBuilder.Schedule().UseCron("*/15 * * * * *").MaxRuns(20)
    );

    // 11. Recurring low-priority task - periodic cleanup
    await dispatcher.Dispatch(
        new LowPriorityTask("Periodic Cache Cleanup", ItemCount: 5, ProcessingTimePerItemMs: 50),
        taskBuilder => taskBuilder.Schedule().Every(2).Minutes().MaxRuns(5)
    );

    logger.LogInformation("=== Successfully dispatched 31 sample tasks across 3 queues ===");
    logger.LogInformation("  - Default queue: 18 tasks");
    logger.LogInformation("  - High-priority queue: 8 tasks (4 immediate + 3 delayed + 1 recurring)");
    logger.LogInformation("  - Low-priority queue: 5 tasks (4 immediate + 1 recurring)");
    logger.LogInformation("Visit http://localhost:5000/evertask to view the monitoring dashboard");
}
