# Template — registration in `Program.cs`

Compose ONLY the lines for chosen features. Keep defaults; don't set knobs without a reason.

## Minimal (dev/test, in-memory)

```csharp
builder.Services.AddEverTask(opt =>
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddMemoryStorage();
```

## Production web app (Postgres + monitoring dashboard + retention)

```csharp
var everTask = builder.Services.AddEverTask(opt => opt
        .RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddPostgresStorage(
        builder.Configuration.GetConnectionString("EverTaskDb")!,
        o => o.SchemaName = "evertask")          // lowercase only for Postgres
    .AddMonitoringApi(o =>
    {
        o.Username = builder.Configuration["EverTask:Monitor:User"] ?? "admin";
        o.Password = builder.Configuration["EverTask:Monitor:Pass"]
                     ?? throw new InvalidOperationException("Set EverTask:Monitor:Pass");
        o.JwtSecret = builder.Configuration["EverTask:Monitor:JwtSecret"]; // set for multi-instance
    });

// Audit retention is on IServiceCollection (NOT the builder):
builder.Services.AddAuditCleanup(
    AuditRetentionPolicy.WithErrorPriority(successRetentionDays: 14, errorRetentionDays: 90));

var app = builder.Build();
// ... your middleware ...
app.MapEverTaskApi();      // dashboard /evertask-monitoring, API /evertask-monitoring/api
app.Run();
```

## SQL Server + Serilog

```csharp
builder.Services.AddEverTask(opt => opt
        .RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddSqlServerStorage(builder.Configuration.GetConnectionString("EverTaskDb")!)
    .AddSerilog(c => c.MinimumLevel.Information()
        .WriteTo.Console()
        .WriteTo.File("logs/evertask-.txt", rollingInterval: RollingInterval.Day));
```

## Worker service (SQL/Postgres, no dashboard)

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddEverTask(opt => opt
        .RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddSqlServerStorage(builder.Configuration.GetConnectionString("EverTaskDb")!);
builder.Services.AddHostedService<RecurringTasksRegistrar>();   // see RecurringRegistrar.md
builder.Build().Run();
```

## Optional blocks (insert inside the `AddEverTask(opt => opt …)` lambda)

```csharp
// concurrency / capacity (only if needed)
.SetMaxDegreeOfParallelism(Environment.ProcessorCount * 4)
.SetChannelOptions(5000)

// resilience defaults (example override values — the framework default is LinearRetryPolicy(3, 500ms), no timeout)
.SetDefaultRetryPolicy(new LinearRetryPolicy(3, TimeSpan.FromSeconds(1)))
.SetDefaultTimeout(TimeSpan.FromMinutes(5))

// audit + persistent handler logs
.SetDefaultAuditLevel(AuditLevel.Full)
.WithPersistentLogger(log => log.SetMinimumLevel(LogLevel.Information).SetMaxLogsPerTask(1000))
```

```csharp
// named queues (chained on the builder, AFTER the AddEverTask(...) call)
.ConfigureDefaultQueue(q => q.SetMaxDegreeOfParallelism(8))
.AddQueue("critical",   q => q.SetMaxDegreeOfParallelism(16).SetChannelCapacity(500))
.AddQueue("background", q => q.SetMaxDegreeOfParallelism(2).SetFullBehavior(QueueFullBehavior.FallbackToDefault))
```

Connection strings live in `appsettings.json` / user-secrets / env — never inline literals.
With `AutoApplyMigrations = true` (default) the schema is created on first run.
