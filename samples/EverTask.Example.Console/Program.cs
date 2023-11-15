using EverTask.Abstractions;
using EverTask.Example.Console;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

var builder = Host.CreateDefaultBuilder(args);
builder.ConfigureServices(services =>
{
    services.AddEverTask(opt =>
            {
                opt.SetChannelOptions(50)
                   .SetThrowIfUnableToPersist(true)
                   .RegisterTasksFromAssembly(typeof(Program).Assembly);
            })
            .AddMemoryStorage()
            .AddSerilog(opt => opt.WriteTo.Console());
});

var host = builder.Build();
await host.StartAsync();

var dispatcher = host.Services.GetRequiredService<ITaskDispatcher>();

await dispatcher.Dispatch(new SampleTaskRequest("Hello World"));

await host.StopAsync();
Console .ReadKey();
