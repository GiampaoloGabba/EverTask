using EverTask.Abstractions;
using EverTask.Example.Console;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateDefaultBuilder(args).ConfigureLogging(logging => logging.AddFilter("Microsoft", LogLevel.Warning));
builder.ConfigureServices(services =>
{
    services.AddEverTask(opt =>
            {
                opt.SetChannelOptions(50)
                   .SetThrowIfUnableToPersist(true)
                   .RegisterTasksFromAssembly(typeof(Program).Assembly);
            })
            .AddMemoryStorage();
});

var host = builder.Build();
await host.StartAsync();

Console.WriteLine($"=== START DISPATCH: {DateTimeOffset.Now}");

var dispatcher = host.Services.GetRequiredService<ITaskDispatcher>();

//await dispatcher.Dispatch(new SampleTaskRequest("Hello World 30 seconds"), TimeSpan.FromSeconds(30));

var scheduleTime = DateTimeOffset.Now.AddSeconds(10);

//await dispatcher.Dispatch(new SampleTaskRequest("Hello World 10 seconds"), scheduleTime);


//await dispatcher.Dispatch(new SampleTaskRequest("|||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||| Hello World every 10 seconds for 3 times** "), taskBuilder => taskBuilder.Schedule().UseCron("*/10 * * * * *").MaxRuns(3));

await dispatcher.Dispatch(
    new SampleTaskRequest(
        "|||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||| Hello World every 10 seconds for 3 times** "),
    taskBuilder => taskBuilder.Schedule().EveryHour().AtMinute(10).MaxRuns(3));

Console.ReadKey();
await host.StopAsync();
