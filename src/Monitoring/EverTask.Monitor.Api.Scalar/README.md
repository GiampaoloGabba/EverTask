# EverTask.Monitor.Api.Scalar

[Scalar](https://scalar.com) API reference UI for the [EverTask](https://github.com/GiampaoloGabba/EverTask) Monitoring API.

Adds an interactive API reference at `/evertask-monitoring/scalar`, rendering the monitoring
OpenAPI document served at `/evertask-monitoring/openapi/evertask-monitoring.json`. Both live
under the monitoring base path and never touch your application's own OpenAPI, Swagger, or
Scalar setup.

## Usage

```csharp
builder.Services
    .AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddMemoryStorage()
    .AddMonitoringApi()
    .AddMonitoringApiScalar(); // after AddMonitoringApi()

var app = builder.Build();
app.MapEverTaskApi();
app.Run();
```

Adding the package enables the monitoring OpenAPI document automatically
(`EnableOpenApiDocument = true`).

## Requirements

- `EverTask.Monitor.Api` 4.0.0+
- .NET 9.0 or later at runtime. On .NET 8.0 the document generator built into ASP.NET Core does
  not exist, so the package logs a warning at startup and serves nothing.

See the [monitoring dashboard documentation](https://github.com/GiampaoloGabba/EverTask/blob/master/docs/monitoring-dashboard.md)
for details.
