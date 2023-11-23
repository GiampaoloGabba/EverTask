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

builder.Services.AddEverTask(opt =>
       {
           opt.SetChannelOptions(50)
              .SetThrowIfUnableToPersist(true)
              .RegisterTasksFromAssembly(typeof(Program).Assembly);
       })
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
