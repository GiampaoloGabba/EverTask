![EverTask Logo](https://raw.githubusercontent.com/GiampaoloGabba/EverTask/master/assets/logo-main.png)

[![Build](https://github.com/GiampaoloGabba/EverTask/actions/workflows/build.yml/badge.svg)](https://github.com/GiampaoloGabba/EverTask/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/vpre/evertask.svg?label=Evertask)](https://www.nuget.org/packages/evertask)
[![NuGet](https://img.shields.io/nuget/vpre/evertask.abstractions.svg?label=Evertask.Abstractions)](https://www.nuget.org/packages/evertask.abstractions)
[![NuGet](https://img.shields.io/nuget/vpre/evertask.sqlserver.svg?label=Evertask.SqlServer)](https://www.nuget.org/packages/evertask.sqlserver)
[![NuGet](https://img.shields.io/nuget/vpre/evertask.serilog.svg?label=Evertask.Serilog)](https://www.nuget.org/packages/evertask.serilog)
[![NuGet](https://img.shields.io/nuget/vpre/EverTask.Monitor.AspnetCore.SignalR.svg?label=EverTask.Monitor.AspnetCore.SignalR)](https://www.nuget.org/packages/EverTask.Monitor.AspnetCore.SignalR)

## Overview

EverTask is a .NET library for executing background tasks in .NET applications. It is designed to be simple and focuses
on task persistence, ensuring that pending tasks resume upon application restart.

> This project is in its initial stages, more detailed documentation will be provided in the future.

## Features

| Feature                          | Description                                                                                                                                                                                                                                                                                                                    |
|----------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Background Task Execution**    | Easily run background tasks with parameters in .NET                                                                                                                                                                                                                                                                            |
| **Persistence**                  | Resumes pending tasks after application restarts.                                                                                                                                                                                                                                                                              |
| **Managed Parallelism**          | Efficiently handles concurrent task execution with configurable parallelism.                                                                                                                                                                                                                                                   |
| **Scheduled and Delayed tasks**  | Schedule tasks for future execution or delay them using a TimeSpan.                                                                                                                                                                                                                                                            |
| **Resilient Task Execution**     | 🌟 Now available! A powerful resilience feature to ensure your tasks are robust against transient failures. Fully customizable even with your custom retry policies                                                                                                                                                            |
| **Task monitoring**              | 🌟 Now available! Monitor your task with the included in-memory monitoring or remotely with SignalR! **Docs coming soon**  [![NuGet](https://img.shields.io/nuget/vpre/EverTask.Monitor.AspnetCore.SignalR.svg?label=EverTask.Monitor.AspnetCore.SignalR)](https://www.nuget.org/packages/EverTask.Monitor.AspnetCore.SignalR) |
| **Error Handling**               | Method overrides for error observation and task completion/cancellation.                                                                                                                                                                                                                                                       |
| **SQL Storage**                  | Includes support for SQL Server storage, enabling persistent task management. <br/>[![NuGet](https://img.shields.io/nuget/vpre/evertask.sqlserver.svg?label=Evertask.SqlServer)](https://www.nuget.org/packages/evertask.sqlserver)                                                                                            |
| **In-Memory Storage**            | Provides an in-memory storage solution for testing and lightweight applications.                                                                                                                                                                                                                                               |
| **Serilog Integration**          | Supports integration with Serilog for detailed and customizable logging. <br/>[![NuGet](https://img.shields.io/nuget/vpre/evertask.serilog.svg?label=Evertask.Serilog)](https://www.nuget.org/packages/evertask.serilog)                                                                                                       |
| **Extensible Storage & Logging** | Designed to allow easy plug-in of additional database solutions or logging systems.                                                                                                                                                                                                                                            |                                                                      |
| **Async All The Way**            | Fully asynchronous architecture, enhancing performance and scalability in modern environments.                                                                                                                                                                                                                                 |
| **Simplicity by Design**         | Created for simplicity, using the latest .NET technologies.                                                                                                                                                                                                                                                                    |
| **Inspiration from MediaTr**     | Implementation based on creating requests and handlers.                                                                                                                                                                                                                                                                        |

## Efficient Task Processing

EverTask employs a non-polling approach for task management, utilizing the .NET's `System.Threading.Channels` to create
a `BoundedQueue`. This queue efficiently manages task execution without the need for constant database polling. Upon
application restart after a stop, any unprocessed tasks are retrieved from the database in bulk and re-queued in the
channel's queue for execution by the background service. This design ensures a seamless and efficient task processing
cycle, even across application restarts.

## Basic Configuration

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddMemoryStorage();
```

## Advanced Configuration

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.SetChannelOptions(500)
       .SetThrowIfUnableToPersist(true)
       .RegisterTasksFromAssembly(typeof(AppSettings).Assembly);
})
.AddSqlServerStorage(configuration.GetConnectionString("QueueWorkerSqlStorage")!,
    opt =>
    {
        opt.SchemaName          = "EverTask";
        opt.AutoApplyMigrations = true;
    })
.AddSerilog(opt => 
    opt.ReadFrom.Configuration(configuration, new ConfigurationReaderOptions { SectionName = "EverTaskSerilog" }));
```

For setting up all configurations, refer to the [Fluent Service Configuration](#fluent-service-configuration) section.

## Creating Requests and Handlers

This example demonstrates how to create a request and its corresponding handler in EverTask. The `SampleTaskRequest`
and `SampleTaskRequestHandler` illustrate the basic structure. Additionally, the handler includes optional overrides
that allow you to control and monitor the lifecycle of a background task, providing hooks for when a task starts,
completes, is disposed, or encounters an error.

```csharp
public record SampleTaskRequest(string TestProperty) : IEverTask;
```

```csharp
public class SampleTaskRequestHanlder : EverTaskHandler<SampleTaskRequest>
{
    private readonly ILogger<SampleTaskRequestHanlder> _logger;

    public SampleTaskRequestHanlder(ILogger<SampleTaskRequestHanlder> logger)
    {
        _logger = logger;
    }

    public override Task Handle(SampleTaskRequest backgroundTask, CancellationToken cancellationToken)
    {
        _logger.LogInformation($"Property value: {backgroundTask.TestProperty}");
        return Task.CompletedTask;
    }
}
```

### `EverTaskHandler` Optional overrides for lifecycle control

```csharp
public override ValueTask OnStarted(Guid taskId)
{
    _logger.LogInformation($"====== TASK WITH ID {taskId} STARTED IN BACKGROUND ======");
    return ValueTask.CompletedTask;
}

public override ValueTask OnCompleted(Guid taskIdtaskId)
{
    _logger.LogInformation($"====== TASK WITH ID {taskId} COMPLETED IN BACKGROUND ======");
    return ValueTask.CompletedTask;
}

public override ValueTask OnError(Guid taskId, Exception? exception, string? message)
{
    _logger.LogError(exception, $"Error in task with ID {taskId}: {message}");
    return ValueTask.CompletedTask;
}

protected override ValueTask DisposeAsyncCore()
{
    _logger.LogInformation("====== TASK DISPOSED IN BACKGROUND ======");
    return base.DisposeAsyncCore();
}
```

## Task Dispatch

To dispatch a task, obtain an instance of `ITaskDispatcher`. This can be done using Dependency Injection:

```csharp
// Retrieving ITaskDispatcher via method injection
var _dispatcher = serviceProvider.GetService<ITaskDispatcher>();

// Alternatively, ITaskDispatcher can be injected directly into the constructor of your class
_dispatcher.Dispatch(new SampleTaskRequest("Hello World"));
```

### Dispatching Tasks with Delay

You can also schedule tasks to be executed after a certain delay. This can be achieved using either `TimeSpan`
or `DateTimeOffset`.

#### Using TimeSpan for Relative Delay

To delay task execution by a relative time period, use `TimeSpan`. This is useful when you want to postpone a task by a
specific duration, such as 30 minutes or 2 hours from now.

```csharp
// Delaying task execution by 30 minutes
var delay = TimeSpan.FromMinutes(30);
_dispatcher.Dispatch(new SampleTaskRequest("Delayed Task"), delay);
```

#### Using DateTimeOffset for Absolute Delay

Alternatively, use `DateTimeOffset` for scheduling a task at a specific future point in time. This is particularly
useful for tasks that need to be executed at a specific date and time, regardless of the current moment.

```csharp
// Scheduling a task for a specific time in the future
var scheduledTime = DateTimeOffset.Now.AddHours(2); // 2 hours from now
_dispatcher.Dispatch(new SampleTaskRequest("Scheduled Task"), scheduledTime);
```

> 💡 **Remember:** Delayed and scheduled tasks are also persistent. If your app restarts, you won't lose these tasks –
> they'll be executed at the right time!

## Task Cancellation

When you dispatch, you can capture the returned GUID to keep track of the task. If you need to cancel this task before
it starts, use the `Cancel` method of `ITaskDispatcher` with this ID.

```csharp
// Dispatching a task and getting its unique ID
Guid taskId = _dispatcher.Dispatch(new SampleTaskRequest("Cancelable Task"));

// Cancelling the task (if not started yet)
_dispatcher.Cancel(taskId);
```

> 💡 **Note:** Cancellation is effective only if the task has not yet begun. Once a task is in progress, the Cancel
> method will not affect its execution.

## Resilience and Retry Policies

EverTask now includes a powerful resilience feature to ensure your tasks are robust against transient failures. This is
achieved through customizable retry policies, which are applied to task executions.

#### Default Linear Retry Policy

By default, tasks are executed using the LinearRetryPolicy. This policy attempts to execute a task three times with a
fixed delay of 500 milliseconds between retries. This approach helps in overcoming temporary issues that might prevent a
task from completing successfully on its first try.

```csharp
// Handler automatically inherits the default LinearRetryPolicy
public class MyTaskHandler : EverTaskHandler<MyTask>
{
    public override Task Handle(CancellationToken cancellationToken)
    {
        // Task handling logic
    }
}
```

#### Customizing Retry Policies

For each task handler, you can define or override the default retry policy settings. This includes changing the number
of execution attempts, fixed execution times, or providing an array of `TimeSpan` values for the retry delays.

Retries with a fixed delay:

```csharp
public class MyCustomRetryTaskHandler : EverTaskHandler<MyCustomRetryTask>
{
    public MyCustomRetryTaskHandler()
    {
        // Setting a custom LinearRetryPolicy with 2 retries and 300ms delay
        RetryPolicy = new LinearRetryPolicy(2, TimeSpan.FromMilliseconds(300));
    }
    
    public override Task Handle(CancellationToken cancellationToken)
    {
        // Task handling logic
    }
}
```

With an array of timespan:

```csharp
public class MyCustomRetryTaskHandler : EverTaskHandler<MyCustomRetryTask>
{
    public MyCustomRetryTaskHandler()
    {
        // Define a LinearRetryPolicy with custom delays
        RetryPolicy = new LinearRetryPolicy(new TimeSpan[] 
        {
            TimeSpan.FromMilliseconds(200),  // First delay
            TimeSpan.FromMilliseconds(300),  // Second delay
            TimeSpan.FromMilliseconds(600)   // Third delay
        });
    }
    
    public override Task Handle(CancellationToken cancellationToken)
    {
        // Task handling logic
    }
}
```

#### Implementing Custom Retry Policies

EverTask's design allows for the implementation of custom retry policies. Create your own policy by implementing
the `IRetryPolicy` interface. This enables you to craft unique retry mechanisms tailored to specific requirements of
your tasks.

```csharp
public class MyCustomRetryPolicy : IRetryPolicy
{
    public async Task Execute(Func<CancellationToken, Task> action, CancellationToken token = default)
    {
        // Implementation of your custom retry logic
    }
}
```

The on your handler:

```csharp
public class MyCustomRetryTaskHandler : EverTaskHandler<MyCustomRetryTask>
{
    public MyCustomRetryTaskHandler()
    {
        // Setting your custom RetryPolicy
        RetryPolicy = new MyCustomRetryPolicy();
    }
    
    public override Task Handle(CancellationToken cancellationToken)
    {
        // Task handling logic
    }
}
```

## Handling WorkerSerivce stops with CancellationToken in Task Handlers

In EverTask, every task handler's `Handle` method is provided with a `CancellationToken`. This token is crucial for
effectively managing task interruptions when the EverTask WorkerService is stopped.

If the WorkerService is halted, the `CancellationToken` is set to a cancelled state. Tasks interrupted in this manner
are marked as `ServiceStopped` and are re-queued upon the application's next restart.

This ensures no tasks are lost due to service stops, and the presence of `CancellationToken` in the `Handle` method
allows for custom logic to track and manage partial execution of tasks.

## Task Continuations and Rescheduling: Advanced Workflow Management

EverTask supports not only task continuations but also the rescheduling of tasks, providing a comprehensive approach to
advanced workflow management.

#### Chaining Tasks for Sequential Execution

You can chain tasks for sequential execution by utilizing the lifecycle methods in task handlers. Overriding methods
like `OnComplete` or `OnError` allows you to dispatch new tasks based on the outcome of the current task.

```csharp
public class MyTaskHandler : EverTaskHandler<MyTaskRequest>
{
    private readonly IEverTaskDispatcher _dispatcher;

    public MyTaskHandler(IEverTaskDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public override ValueTask OnCompleted(Guid taskId)
    {
        // Dispatch another task upon completion
        _dispatcher.Dispatch(new AnotherTaskRequest());
        return base.OnCompleted(taskId);
    }
}
```

#### Task Rescheduling via Lifecycle Methods

In addition to task chaining, EverTask also enables task rescheduling within the same lifecycle methods. This feature is
particularly useful for tasks that need to be executed repeatedly or at different intervals, depending on certain
conditions or outcomes.

```csharp
public class MyReschedulingTaskHandler : EverTaskHandler<MyTaskRequest>
{
    private readonly IEverTaskDispatcher _dispatcher;

    public MyReschedulingTaskHandler(IEverTaskDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public override ValueTask OnCompleted(Guid taskId)
    {
        // Reschedule the same task based on certain conditions or logic
        _dispatcher.Dispatch(new MyTaskRequest(), TimeSpan.FromMinutes(30)); // Rescheduling after 30 minutes
        return base.OnCompleted(taskId);
    }
}
```

With these capabilities, EverTask offers unparalleled flexibility in creating sophisticated, custom-tailored workflows
for background task processing. Whether you need to chain tasks sequentially or reschedule them based on specific
criteria, EverTask provides the necessary tools for robust and efficient task management.

## Task Monitoring in EverTask

EverTask provides basic task monitoring through the `TaskEventOccurredAsync` event, accessible via dependency injection from `IEverTaskWorkerExecutor`. This event aggregates all the events generated by EverTask's WorkerService. Here's an example of how it can be utilized:

In your service class, you can subscribe to the `TaskEventOccurredAsync` event. For instance:
```csharp
public class MyService
{
    public MyService(IEverTaskWorkerExecutor executor, ILogger<EverTaskTestController> logger)
    {
        executor.TaskEventOccurredAsync += data =>
        {
            logger.LogInformation("Message received from EverTask Worker Server: {@eventData}", data);
            return Task.CompletedTask;
        };
    }
}
```
The above code will produce this output:
```
Message received from EverTask Worker Server: EverTaskEventData { TaskId = dc49351d-476d-49f0-a1e8-3e2a39182d22, EventDateUtc = 19/11/2023 16:10:20 +00:00, Severity = Information, TaskType = EverTask.Example.AspnetCore.SampleTaskRequest, TaskHandlerType = EverTask.Example.AspnetCore.SampleTaskRequestHanlder, TaskParameters = {"TestProperty":"Hello World"}, Message = Task with id dc49351d-476d-49f0-a1e8-3e2a39182d22 was completed., Exception =  }
```


Here, `data` is of type `EverTaskEventData` which includes detailed information about the event:
```csharp
public record EverTaskEventData(
    Guid TaskId,
    DateTimeOffset EventDateUtc,
    string Severity,
    string TaskType,
    string TaskHandlerType,
    string TaskParameters,
    string Message,
    string? Exception = null);

//Possible enum values
public enum SeverityLevel
{
    Information,
    Warning,
    Error
}
```
This allows users to trigger custom code based on the events produced by EverTask.

### Real-Time Monitoring with SignalR
[![NuGet](https://img.shields.io/nuget/vpre/EverTask.Monitor.AspnetCore.SignalR.svg?label=EverTask.Monitor.AspnetCore.SignalR)](https://www.nuget.org/packages/EverTask.Monitor.AspnetCore.SignalR)

Additionally, EverTask offers real-time monitoring through a SignalR hub, available with the `EverTask.Monitoring.AspnetCore.SignalR` package for ASP.NET Core applications.

To use it, first register the service in your startup configuration:

```csharp
builder.Services.AddEverTask(opt =>
       {
           opt
              .RegisterTasksFromAssembly(typeof(Program).Assembly);
       })
       .AddMemoryStorage()
       .AddSignalRMonitoring();
```
Then, register the middleware:
```csharp
app.MapEverTaskMonitorHub();
```
This creates a SignalR hub at the URL `/evertask/monitoring`. You can customize this URL, for example:
```csharp
app.MapEverTaskMonitorHub("/task-monitoring");
```
The hub sends all events in real-time to all connected clients. The event type is always `EverTaskEventData`.

Future updates will include new monitoring systems such as Email Alerts, ApplicationInsights, OpenTelemetry, etc.

> 💡 **Note:**  Depending on EverTask's configuration, all events are also written to the logs as per the chosen configuration. Additionally, if SQL persistence is used, the task status and its audit trail can always be checked in the database.

&nbsp;

---

<a name="fluent-service-configuration"></a>
# Fluent Service Configuration

`EverTaskService` can be configured using a series of fluent methods, allowing a clear and user-friendly way to set up
the service. These methods enable precise control over task processing, persistence, and parallel execution. Below are
the available configuration methods, along with their default values and types:

### `SetChannelOptions (Overloaded Methods)`

- **Type:** `Action<BoundedChannelOptions>` or `int`
- **Default:** Capacity set to 100, `FullMode` set to `BoundedChannelFullMode.Wait`
- **Functionality:** Configures the behavior of the task queue. You can directly specify the queue capacity or provide
  a `BoundedChannelOptions` object. This defines the maximum number of tasks that can be queued and the behavior when
  the queue is full.

### `SetThrowIfUnableToPersist`

- **Type:** `bool`
- **Default:** `true`
- **Functionality:** Determines whether the service should throw an exception if it is unable to persist a task. When
  enabled, it ensures that task persistence failures are explicitly managed, aiding in data integrity.

### `SetMaxDegreeOfParallelism`

- **Type:** `int`
- **Default:** `1`
- **Functionality:** Sets the maximum number of tasks that can be executed concurrently. The default sequential
  execution can be adjusted to enable parallel processing, optimizing task throughput in multi-core systems.

### `RegisterTasksFromAssembly`

- **Functionality:** Facilitates the registration of task handlers from a single assembly. This is particularly
  beneficial for applications structured in a modular fashion, enabling easy integration of task handlers.

### `RegisterTasksFromAssemblies`

- **Functionality:** Allows for the registration of task handlers from multiple assemblies. This approach suits larger
  applications with distributed task handling logic spread across various modules or libraries.

## SQL Server Persistence

[![NuGet](https://img.shields.io/nuget/vpre/evertask.sqlserver.svg?label=Evertask.SqlServer)](https://www.nuget.org/packages/evertask.sqlserver)

- **Schema Management:** EverTask creates a new schema named `EverTask` by default. This approach avoids adding clutter
  to the main data schema.
- **Schema Customization:** Allows specifying a different schema or using `null` to default to the main schema.
- **Migration Table:** If using a custom Schema, event the Entity Framework Core migration table will be placed in that
  schema.
- **Migration Handling:** Option to apply database migrations automatically or handle them manually.

## Serilog Integration

[![NuGet](https://img.shields.io/nuget/vpre/evertask.serilog.svg?label=Evertask.SeriLog)](https://www.nuget.org/packages/evertask.serilog)

- **Default Logging:** Uses .NET configured `ILogger` by default.
- **Serilog Option:** Enables adding Serilog as a separate logger for EverTask, with customizable options.
- **Example Configuration in appsettings.json:**
  ```json
  "EverTaskSerilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Default": "Information",
        "Microsoft": "Warning",
        "Microsoft.AspNetCore.SpaProxy": "Information",
        "Microsoft.Hosting.Lifetime": "Information",
        "Microsoft.EntityFrameworkCore.Database.Command": "Information"
      }
    },
    "WriteTo": [
      {
        "Name": "Console"
      },
      {
        "Name": "File",
        "Args": {
          "path": "Logs/evertask-log-.txt",
          "rollingInterval": "Day",
          "fileSizeLimitBytes": 10485760,
          "retainedFileCountLimit": 10,
          "shared": true,
          "flushToDiskInterval": "00:00:01"
        }
      }
    ],
    "Enrich": [
      "FromLogContext",
      "WithMachineName",
      "WithThreadId"
    ],
    "Properties": {
      "Application": "CliClub"
    }
  }

## EverTask and EverTask.Abstractions

[![NuGet](https://img.shields.io/nuget/vpre/evertask.svg?label=Evertask)](https://www.nuget.org/packages/evertask)
[![NuGet](https://img.shields.io/nuget/vpre/evertask.abstractions.svg?label=Evertask.Abstractions)](https://www.nuget.org/packages/evertask.abstractions)

EverTask is complemented by the `EverTask.Abstractions` package, designed for use in Application projects where
additional implementations are not required. This allows separation of concerns, keeping your application layer free
from infrastructural code.

In your Infrastructure project, where EverTask is added, specify the assembly (or assemblies) containing `IEverTask`
requests. This modular approach ensures that the application layer remains clean and focused, while the infrastructure
layer handles task execution and management.

## Serialization and deserialization of Requests for Persistence

EverTask uses Newtonsoft.Json for serializing and deserializing task requests, due to its robust support for
polymorphism and inheritance, features that are limited in System.Text.Json. It is recommended to use simple objects for
task requests, preferably primitives or uncomplicated complex objects, to ensure smooth serialization. In cases where
EverTask is unable to serialize a request, it will throw an exception during the `Dispatch` method. This design choice
emphasizes reliability in task persistence, ensuring that only serializable tasks are queued for execution.

## Future Developments

| Feature                                | Description                                                                                                                                                                                       |
|----------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Web Dashboard**                      | Implement a simple web dashboard for monitoring tasks. Also there will be some management capability (start/stop a task, change some execution parameters)                                        |
| **Recurring Tasks**                    | Recurring tasks using cron expressions or fluent builder.                                                                                                                                         |
| **Configurable timeout**               | If you want to stop a task that is running for too much time                                                                                                                                      |
| **Support for New monitoring Options** | Email alerts, application insights integration, open telemetry integration, ecc..                                                                                                                 |
| **Support for New Storage Options**    | Considering the inclusion of additional storage options like Redis, MySql, Postgres, and various DocumentDBs initially supported by EfCore, with the possibility of expanding to other databases. |
| **Clustering tasks**                   | I'm toying with the idea to allow multiple server running evertask to create a simple cluster for tasks execution, with rules like loading balance, fail-over                                     |
| **Improving documentation**            | docs needs more love...                                                                                                                                                                           |

&nbsp;

## 🌟 Acknowledgements

Special thanks to **[jbogard](https://github.com/jbogard)** for the **[MediaTr](https://github.com/jbogard/MediatR)**
project, providing significant inspiration in the development of key components of this library, especially in the
creation of:

[`TaskDispatcher.cs`](https://github.com/GiampaoloGabba/EverTask/blob/master/src/EverTask/TaskDispatcher.cs)

[`TaskHandlerExecutor.cs`](https://github.com/GiampaoloGabba/EverTask/blob/master/src/EverTask/Handler/TaskHandlerExecutor.cs)

[`TaskHandlerWrapper.cs`](https://github.com/GiampaoloGabba/EverTask/blob/master/src/EverTask/Handler/TaskHandlerWrapper.cs)

[`HandlerRegistrar.cs`](https://github.com/GiampaoloGabba/EverTask/blob/master/src/EverTask/MicrosoftExtensionsDI/HandlerRegistrar.cs)

I have included comments within these files to acknowledge and reference the specific parts of the MediaTr project that
inspired them.

Their approach and architecture have been instrumental in shaping the functionality and design of these elements.

This project includes code from [MediatR](https://github.com/jbogard/MediatR), which is licensed under the Apache 2.0
License. The full text of the license can be found in the [LICENSE](LICENSE) file.



