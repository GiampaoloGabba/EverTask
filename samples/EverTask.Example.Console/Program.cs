using EverTask.Abstractions;
using EverTask.Example.Console;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateDefaultBuilder(args);
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

await dispatcher.Dispatch(new SampleTaskRequest("Hello World 30 seconds"), TimeSpan.FromSeconds(30));

var scheduleTime = DateTimeOffset.Now.AddSeconds(10);

await dispatcher.Dispatch(new SampleTaskRequest("Hello World 10 seconds"), scheduleTime);

Console.ReadKey();
await host.StopAsync();
