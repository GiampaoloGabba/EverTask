using EverTask.Abstractions;
using EverTask.Example.Console;
using EverTask.Monitor.Api.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Configure logging
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

// Configure EverTask with monitoring API and dashboard
builder.Services.AddEverTask(opt =>
{
    opt.SetChannelOptions(50)
       .SetThrowIfUnableToPersist(true)
       .RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddMemoryStorage()
.AddMonitoringApi(options =>
{
    options.EnableUI             = true;
    options.Username             = "admin";
    options.Password             = "admin";
    options.EnableAuthentication = true;
});

var app = builder.Build();

// Map EverTask monitoring API and dashboard
app.MapEverTaskApi();

// Start the web server
await app.StartAsync();

Console.WriteLine("===========================================");
Console.WriteLine("EverTask Example with Monitoring Dashboard");
Console.WriteLine("===========================================");
Console.WriteLine($"API:       http://localhost:5000/evertask-monitoring/api");
Console.WriteLine($"Dashboard: http://localhost:5000/evertask-monitoring");
Console.WriteLine($"Username:  admin");
Console.WriteLine($"Password:  admin");
Console.WriteLine("===========================================");
Console.WriteLine($"Started at: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
Console.WriteLine("===========================================");
Console.WriteLine();

// Dispatch some example tasks
var dispatcher = app.Services.GetRequiredService<ITaskDispatcher>();

Console.WriteLine("Dispatching example tasks...");

// Immediate task
await dispatcher.Dispatch(new SampleTaskRequest("Immediate task - Hello World!"));
Console.WriteLine("✓ Dispatched immediate task");

// Delayed task (10 seconds)
await dispatcher.Dispatch(new SampleTaskRequest("Delayed task - Hello in 10 seconds"), TimeSpan.FromSeconds(10));
Console.WriteLine("✓ Dispatched delayed task (10 seconds)");

// Scheduled task (specific time)
var scheduleTime = DateTimeOffset.Now.AddSeconds(30);
await dispatcher.Dispatch(new SampleTaskRequest($"Scheduled task - Hello at {scheduleTime:HH:mm:ss}"), scheduleTime);
Console.WriteLine($"✓ Dispatched scheduled task (at {scheduleTime:HH:mm:ss})");

// Recurring task (every minute, max 5 runs)
await dispatcher.Dispatch(
    new SampleTaskRequest("Recurring task - Hello every minute"),
    taskBuilder => taskBuilder.Schedule().EveryMinute().MaxRuns(5)
);
Console.WriteLine("✓ Dispatched recurring task (every minute, max 5 runs)");

// Recurring task with cron (every 30 seconds, max 10 runs)
await dispatcher.Dispatch(
    new SampleTaskRequest("Recurring task - Hello every 30 seconds"),
    taskBuilder => taskBuilder.Schedule().UseCron("*/30 * * * * *").MaxRuns(10)
);
Console.WriteLine("✓ Dispatched recurring cron task (every 30 seconds, max 10 runs)");

// Daily recurring task (max 3 runs)
await dispatcher.Dispatch(
    new SampleTaskRequest("Daily task - Hello every day"),
    taskBuilder => taskBuilder.Schedule().EveryDay().MaxRuns(3)
);
Console.WriteLine("✓ Dispatched daily recurring task (max 3 runs)");

Console.WriteLine();
Console.WriteLine("All example tasks dispatched!");
Console.WriteLine();
Console.WriteLine("Visit the dashboard to monitor task execution:");
Console.WriteLine("→ http://localhost:5000/evertask-monitoring");
Console.WriteLine();
Console.WriteLine("Press any key to stop the application...");
Console.WriteLine();

Console.ReadKey();
await app.StopAsync();
