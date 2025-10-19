using EverTask.Logging.Serilog;
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
});

builder.Services.AddSignalR();

// Configure EverTask with multiple queues for workload isolation
builder.Services.AddEverTask(opt =>
       {
           opt.SetThrowIfUnableToPersist(true)
              .RegisterTasksFromAssembly(typeof(Program).Assembly);
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
       .AddQueue("background", q => q
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
       .AddSerilog(opt => opt.ReadFrom.Configuration(builder.Configuration, new ConfigurationReaderOptions { SectionName = "EverTaskSerilog" }));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.MapEverTaskMonitorHub();

app.Run();
