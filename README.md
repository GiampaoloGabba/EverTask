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

> More detailed documentation will be provided in the future.

## Features

- **Background execution**:<br>
  Easily run background tasks with parameters in .NET
- **Persistence**<br>Resumes pending tasks after application restarts.
- **Managed Parallelism**<br>Efficiently handles concurrent task execution with configurable parallelism.
- **Scheduled, Delayed and recurring tasks**<br>Schedule tasks for future execution or delay them using a TimeSpan. You can also create recurring tasks, with cron or with a powerful fluent builder!
- **Resilient execution**<br>A powerful resilience feature to ensure your tasks are robust against transient failures. Fully customizable even with your custom retry policies, easily integrable with [Polly](https://github.com/App-vNext/Polly)
- **Optional specialized CPU-bound execution**<br>Optimized handling for CPU-intensive tasks with an option to execute in a separate thread, ensuring efficient processing without impacting I/O-bound operations. Use judiciously for tasks requiring significant computational resources.
- **Timeout management**<br>Configure the maximum execution time for your tasks.
- **Error Handling**<br>Method overrides for error observation and task completion/cancellation.
- **Monitoring (local and remote)**<br>Monitor your task with the included in-memory monitoring or remotely with SignalR!<br>[![NuGet](https://img.shields.io/nuget/vpre/EverTask.Monitor.AspnetCore.SignalR.svg?label=EverTask.Monitor.AspnetCore.SignalR)](https://www.nuget.org/packages/EverTask.Monitor.AspnetCore.SignalR)
- **SQL Storage**<br>Includes support for SQL Server storage, enabling persistent task management.<br>[![NuGet](https://img.shields.io/nuget/vpre/evertask.sqlserver.svg?label=Evertask.SqlServer)](https://www.nuget.org/packages/evertask.sqlserver)
- **In-Memory Storage**<br>Provides an in-memory storage solution for testing and lightweight applications.
- **Serilog Integration**<br>Supports integration with Serilog for detailed and customizable logging.<br>[![NuGet](https://img.shields.io/nuget/vpre/evertask.serilog.svg?label=Evertask.Serilog)](https://www.nuget.org/packages/evertask.serilog)
- **Extensible Storage & Logging**<br>Designed to allow easy plug-in of additional database solutions or logging systems.
- **Async All The Way**<br>Fully asynchronous architecture, enhancing performance and scalability in modern environments.
- **Simplicity by Design**<br>Created for simplicity, using the latest .NET technologies.
- **Inspiration from MediaTr**<br>Implementation based on creating requests and handlers.

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

## Recurring Tasks
In addition to delayed and scheduled tasks, you can also configure recurring tasks. These tasks repeat at specified intervals, providing a powerful way to automate ongoing processes.

You can schedule recurring tasks using various approaches, with cron and with a fluent scheduler. Below are some examples with explanations:

### Fluent Scheduling with EverTask
EverTask's powerful fluent builder offers a wide range of scheduling capabilities for your background tasks. Whether you need simple scheduling or complex recurring patterns, the fluent builder simplifies the process with an intuitive and readable syntax. Here are some examples demonstrating different ways to configure your tasks:

#### Basic Scheduling Examples
```csharp
// Scheduling a task to run every minute at the 30th second
await dispatcher.Dispatch(new SampleTaskRequest("Test"),
builder => builder.Schedule().EveryMinute().AtSecond(30));

// Scheduling a task to run every hour at the 45th minute, limited to 10 runs
await dispatcher.Dispatch(new SampleTaskRequest("Test"),
builder => builder.RunNow().Then().EveryHour().AtMinute(45).MaxRuns(10));

// Scheduling a task to run daily at specific times
var times = new[] { new TimeOnly(12, 0), new TimeOnly(18, 0) };
await dispatcher.Dispatch(new SampleTaskRequest("Test"),
builder => builder.Schedule().Every(3).Days().AtTimes(times));
```

#### Advanced Recurring Schedules
```csharp

// Scheduling a task to run on specific days of the week
var days = new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday };
await dispatcher.Dispatch(new SampleTaskRequest("Test"), 
    builder => builder.Schedule().EveryMonth().OnDays(days));

// Running a task immediately, then every 2 month on the 15th
await dispatcher.Dispatch(new SampleTaskRequest("Test"), 
    builder => builder.RunNow().Then().Every(2).Months().OnDay(15));

// Scheduling a task to run on the first Monday of every month
await dispatcher.Dispatch(new SampleTaskRequest("First Monday"), 
    builder => builder.Schedule().EveryMonth().OnFirst(DayOfWeek.Monday));

// Scheduling a task to run on every second month, starting from February
int[] everyOtherMonth = { 2, 4, 6, 8, 10, 12 };
await dispatcher.Dispatch(new SampleTaskRequest("Bi-monthly"), 
    builder => builder.Schedule().OnMonths(everyOtherMonth));
```

#### Delayed and Scheduled Task Execution
```csharp
// Delaying task execution by 30 seconds
await dispatcher.Dispatch(new SampleTaskRequest("Delayed"), 
    builder => builder.RunDelayed(TimeSpan.FromSeconds(30)));

// Scheduling a task for a specific future time
var dateTimeOffset = new DateTimeOffset(2023, 12, 25, 10, 0, 0, TimeSpan.Zero);
await dispatcher.Dispatch(new SampleTaskRequest("Scheduled"), 
    builder => builder.RunAt(dateTimeOffset));
```

#### Combining Various Scheduling Techniques
```csharp
// Running a task now, then scheduling it to run every 2 hours at the 15th minute
await dispatcher.Dispatch(new SampleTaskRequest("Combined"), 
    builder => builder.RunNow().Then().Every(2).Hours().AtMinute(15));

// Delaying the first run of a task, then executing it every day at noon
await dispatcher.Dispatch(new SampleTaskRequest("Delayed Daily"), 
    builder => builder.RunDelayed(TimeSpan.FromMinutes(10)).Then().EveryDay().AtTime(new TimeOnly(12, 0)));

// Scheduling a task to run on specific months (January, April, July, October)
int[] specificMonths = { 1, 4, 7, 10 };
await dispatcher.Dispatch(new SampleTaskRequest("Quarterly"), 
    builder => builder.RunAt(DateTimeOffset.UtcNow.AddHours(1)).Then().OnMonths(specificMonths));
```

#### Customizing Maximum Runs
```csharp
// Scheduling a task to run every day, with a maximum of 5 executions
await dispatcher.Dispatch(new SampleTaskRequest("Max Runs"), 
    builder => builder.Schedule().EveryDay().MaxRuns(5));
```

These examples illustrate the flexibility and power of EverTask's fluent builder. With its comprehensive range of scheduling options, EverTask makes it easy to configure and manage background tasks in your .NET applications.

### Scheduling with Cron Expression
Cron expressions are powerful tools for defining complex time-based schedules. Originating from Unix systems, they provide a concise way to specify patterns for recurring tasks. A cron expression consists of fields representing different time units, like minutes, hours, days, and months.

With EverTask, you can leverage the power of cron expressions to schedule tasks with great flexibility. Whether you need a task to run every hour, on specific days of the week, or at a particular time each month, cron expressions make it possible.

Implementing Cron Scheduling in EverTask
Here are some examples of using cron expressions in EverTask to schedule tasks:


#### Immediate Execution with Cron Schedule:
```csharp
// Execute task immediately, then repeat according to a Cron schedule
await _dispatcher.Dispatch(task, builder => builder.RunNow().Then().UseCron("*/2 * * * *").MaxRuns(3));
```
#### Delayed Start with Cron Schedule:
```csharp
// Execute task after a short delay, then repeat according to a Cron schedule
await _dispatcher.Dispatch(task, builder => builder.RunDelayed(TimeSpan.FromSeconds(0.5)).Then().UseCron("*/2 * * * *"));
```

#### Scheduled Start with Cron Schedule
```csharp
// Schedule task to start at a specific time, then repeat according to a Cron schedule
await _dispatcher.Dispatch(task, builder => builder.RunAt(dateTimeOffset)).Then().UseCron("*/2 * * * *").MaxRuns(3));
```

#### Every 30 Minutes
```csharp
var cronEvery30Minutes = "*/30 * * * *";
await dispatcher.Dispatch(new SampleTaskRequest("Every 30 Minutes"), 
    builder => builder.Schedule().UseCron(cronEvery30Minutes));
```

#### Every Day at Noon
```csharp
var cronAtNoon = "0 12 * * *";
await dispatcher.Dispatch(new SampleTaskRequest("Daily at Noon"), 
    builder => builder.Schedule().UseCron(cronAtNoon));
```

#### Every Monday Morning
```csharp
var cronEveryMondayMorning = "0 8 * * 1"; // 8 AM on Monday
await dispatcher.Dispatch(new SampleTaskRequest("Every Monday Morning"), 
    builder => builder.Schedule().UseCron(cronEveryMondayMorning));
```

<hr>

> 💡 **Remember:** Delayed, scheduled and recurring tasks are also persistent. If your app restarts, you won't lose these tasks –
> they'll be executed at the right time!

## Task Cancellation

When you dispatch, you can capture the returned GUID to keep track of the task. If you need to cancel this task before
it starts, use the `Cancel` method of `ITaskDispatcher` with this ID.

```csharp
// Dispatching a task and getting its unique ID
Guid taskId = _dispatcher.Dispatch(new SampleTaskRequest("Cancelable Task"));

// Cancelling the task
_dispatcher.Cancel(taskId);
```

> 💡 **Note:** The `Cancel` method triggers the `CancellationToken` in the task's `Handle` method. Tasks should check this token regularly to enable cooperative cancellation. Remember, this only affects tasks in progress; tasks cancelled before execution won't run.


## Task Execution Timeout

EverTask provides a flexible approach to managing task execution times with its timeout functionality.

#### Global Default Timeout

A global default timeout for all tasks can be set using the `SetDefaultTimeout` option in the EverTask configuration. This timeout defines a uniform maximum duration for task execution, after which the `CancellationToken` will be marked as cancelled.

```csharp
// Example of setting a global default timeout
builder.Services.AddEverTask(opt =>
{
    opt.SetDefaultTimeout(TimeSpan.FromMinutes(5)); // Sets a global timeout of 5 minutes
});
```

#### Customizing Timeout per Task Handler
In addition to the global timeout, individual task handlers can specify their own timeout periods. This is achieved by setting the Timeout property in the task handler, allowing for task-specific timeout durations.

```csharp
public class MyCustomTimeoutTaskHandler : EverTaskHandler<MyTask>
{
    public MyCustomTimeoutTaskHandler()
    {
        // Setting a custom timeout for this specific handler
        Timeout = TimeSpan.FromMinutes(2);
    }
    
    public override Task Handle(CancellationToken cancellationToken)
    {
        // Task handling logic
    }
}
```

## CPU-bound Task Handling
In addition to its default async-await behavior, which is ideal for I/O-bound operations (like database operations, email sending, file generation, etc.), EverTask provides a specialized handling mechanism for CPU-bound tasks. This is crucial for tasks that involve intensive computational work, such as data processing or complex calculations.

This is controlled by the `CpuBoundOperation` property in the `EverTaskHandler`.

```csharp
public class MyCustomCPUIntensiveTaskHandler : EverTaskHandler<MyCPUIntensiveTask>
{
    public MyCustomCPUIntensiveTaskHandler()
    {
        CpuBoundOperation = true;
    }

    public override Task Handle(CancellationToken cancellationToken)
    {
        // CPU-intensive task logic
    }
}
```

> 💡 **Note:** When using CpuBoundOperation to execute tasks in a separate thread, be mindful of potential overhead and concurrency issues. This feature is best used for genuinely CPU-intensive tasks. Excessive use can lead to increased resource utilization and complexity.

## Resilience and Retry Policies

EverTask now includes a powerful resilience feature to ensure your tasks are robust against transient failures. This is
achieved through customizable retry policies, which are applied to task executions.

#### Default Linear Retry Policy

Tasks by default use the `LinearRetryPolicy`, set in the global configuration (`SetDefaultRetryPolicy`). This default policy attempts three executions with a 500-millisecond delay between them, addressing temporary issues that might hinder task completion.

You can also set your customized global deault policy:

```csharp
// Example of setting a customized global default RetryPolicy
builder.Services.AddEverTask(opt =>
{
    opt.SetDefaultRetryPolicy(new LinearRetryPolicy(4, TimeSpan.FromMilliseconds(200)));
});
```

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

#### Customizing Retry Policies per task

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

EverTask's design allows for the implementation of custom retry policies. You can create your own policy by implementing the `IRetryPolicy` interface. This enables you to craft unique retry mechanisms tailored to the specific requirements of your tasks.

Below is an example of implementing a custom retry policy using [Polly](https://github.com/App-vNext/Polly):


```csharp
using Polly;

using Polly;
using System;
using System.Threading;
using System.Threading.Tasks;

public class MyCustomRetryPolicy : IRetryPolicy
{
    private readonly AsyncRetryPolicy _pollyRetryPolicy;

    public MyCustomRetryPolicy()
    {
        // Define your Polly retry policy here.
        // For example, a policy that retries three times with an exponential backoff.
        _pollyRetryPolicy = Policy
            .Handle<Exception>() // Specify the exceptions you want to handle/retry on
            .WaitAndRetryAsync(3, retryAttempt =>
                TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), // exponential back-off: 2, 4, 8 seconds
                onRetry: (exception, timeSpan, retryCount, context) =>
                {
                    // You can log the retry attempt here if needed
                }
            );
    }

    public async Task Execute(Func<CancellationToken, Task> action, CancellationToken token = default)
    {
        // Use Polly's ExecuteAsync method to apply the retry policy to the passed action.
        // The passed CancellationToken is respected in the retry policy.
        await _pollyRetryPolicy.ExecuteAsync(async (ct) =>
        {
            await action(ct);
        }, token);
    }
}
```

> 💡 **Note:** The `CancellationToken` provided to the policy is signaled as canceled when the task is halted using `.Cancel`, or if the worker service stops. This ensures that your custom policy remains synchronized with the task's lifecycle.



Then on your handler:

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
Of course, you can also set your polly policy as a default if you wish:
```csharp
// Example of setting a customized global default RetryPolicy
builder.Services.AddEverTask(opt =>
{
    opt.SetDefaultRetryPolicy(new MyCustomRetryPolicy());
});
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
- **Default:** Capacity set to 500, `FullMode` set to `BoundedChannelFullMode.Wait`
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

### `SetDefaultRetryPolicy`

- **Type:** `IRetryPolicy`
- **Default:** `LinearRetryPolicy` *(with 3 tries every 500 milliseconds)*
- **Functionality:** Defines a global default retry policy for tasks, using `LinearRetryPolicy` (3 attempts, 500 ms delay) unless overridden in task handlers. Supports custom policies via `IRetryPolicy` interface implementation.

### `SetDefaultTimeout`

- **Type:** `TimeSpan?`
- **Default:** `null`
- **Functionality:** Specifies a global default timeout for tasks. If set, the `CancellationToken` provided to task handlers will be cancelled after the timeout duration. Users must handle this cancellation in their task logic. Setting to `null` means tasks have no default timeout and will run until completion or external cancellation.

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

| Feature                               | Description                                                                                                                                                                                       |
|---------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Web Dashboard**                     | Implement a simple web dashboard for monitoring tasks. Also there will be some management capability (start/stop a task, change some execution parameters)                                        |
| **WebApi**                  | Webapi endpoints to list and manage tasks execution in EverTask remotely                                                                                                                          |
| **Fluent builder for recurring Tasks** | Recurring tasks using a fluent builder.                                                                                                                                                           |
| **Support for new monitoring Options** | Email alerts, application insights integration, open telemetry integration, ecc..                                                                                                                 |
| **Support for new Storage Options**   | Considering the inclusion of additional storage options like Redis, MySql, Postgres, and various DocumentDBs initially supported by EfCore, with the possibility of expanding to other databases. |
| **Queue customization**               | Create custom queues (each with his own, custom, degree of parallelism) to split task execution (for example by priority)                                                                         |
| **Clustering tasks**                  | I'm toying with the idea to allow multiple server running evertask to create a simple cluster for tasks execution, with rules like loading balance, fail-over                                     |
| **Improving documentation**           | docs needs more love...                                                                                                                                                                           |

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



