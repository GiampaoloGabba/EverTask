![EverTask Logo](https://raw.githubusercontent.com/GiampaoloGabba/EverTask/master/assets/logo-main.png)

[![Build](https://github.com/GiampaoloGabba/EverTask/actions/workflows/build.yml/badge.svg)](https://github.com/GiampaoloGabba/EverTask/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/vpre/evertask.svg)](https://www.nuget.org/packages/evertask)

## Overview
EverTask is a .NET library for executing background tasks in .NET applications. It is designed to be simple and focuses on task persistence, ensuring that pending tasks resume upon application restart.

> This project is in its initial stages, more detailed documentation will be provided in the future.

## Features
| Feature                       | Description                                                                         |
|-------------------------------|-------------------------------------------------------------------------------------|
| **Background Task Execution** | Easily run background tasks with parameters in .NET                                 |
| **Persistence**               | Resumes pending tasks after application restarts.                                   |
| **Simplicity by Design**      | Created for simplicity, using the latest .NET technologies.                         |
| **Inspiration from MediaTr**  | Implementation based on creating requests and handlers.                             |
| **Error Handling**            | Method overrides for error observation and task completion.                         |
| **In-Memory Storage**         | Provides an in-memory storage solution for testing and lightweight applications.    |
| **SQL Storage**                   | Includes support for SQL Server storage, enabling persistent task management.       |
| **Serilog Integration**           | Supports integration with Serilog for detailed and customizable logging.            |
| **Extensible Storage & Logging**  | Designed to allow easy plug-in of additional database solutions or logging systems. |                                                                      |


## Efficient Task Processing

EverTask employs a non-polling approach for task management, utilizing the .NET's `System.Threading.Channels` to create a `BoundedQueue`. This queue efficiently manages task execution without the need for constant database polling. Upon application restart after a stop, any unprocessed tasks are retrieved from the database in bulk and re-queued in the channel's queue for execution by the background service. This design ensures a seamless and efficient task processing cycle, even across application restarts.

## Implementation Example
```csharp
public record SampleTaskRequest(string TestProperty) : IEverTask;

public class SampleTaskRequestHanlder : EverTaskHandler<SampleTaskRequest>
{
    private readonly ILogger<SampleTaskRequestHanlder> _logger;

    public SampleTaskRequestHanlder(ILogger<SampleTaskRequestHanlder> logger)
    {
        _logger = logger;
    }
    public override Task Handle(SampleTaskRequest backgroundTask, CancellationToken cancellationToken)
    {
        _logger.LogInformation(backgroundTask.TestProperty);
        return Task.CompletedTask;
    }

    // Optional Overrides
    public override ValueTask Completed()
    {
        return base.Completed();
    }

    public override ValueTask OnError(Exception? exception, string? message)
    {
        return base.OnError(exception, message);
    }
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

## Basic Configuration
```csharp
builder.Services.AddEverTask(opt =>
{
    opt.SetChannelOptions(50)
       .SetThrowIfUnableToPersist(true)
       .RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddMemoryStorage();
```

## Advanced Configuration
```csharp
builder.Services.AddEverTask(opt =>
{
    opt.SetChannelOptions(settingsManager.Settings.Properties.BackgroundQueueCapacity)
       .SetThrowIfUnableToPersist(true)
       .RegisterTasksFromAssembly(typeof(AppSettings).Assembly);
})
.AddSqlServerStorage(configuration.GetConnectionString("QueueWorkerSqlStorage")!,
    opt =>
    {
        opt.SchemaName          = "EverTask";
        opt.AutoApplyMigrations = true;
    })
.AddSerilog(opt => opt.ReadFrom.Configuration(configuration, new ConfigurationReaderOptions { SectionName = "EverTaskSerilog" }));
```

# Service Configuration Properties

`EverTaskServiceConfiguration` provides various options for configuring the EverTask service. Here's a summary of its properties and their intended functionalities:

### `ChannelOptions`
- **Type:** `BoundedChannelOptions`
- **Default:** Capacity set to 100, `FullMode` set to `BoundedChannelFullMode.Wait`
- **Purpose:** Defines the behavior of the task queue. The capacity determines the maximum number of tasks that can be queued, and `FullMode` decides the behavior when the queue is full (e.g., waiting for space to become available).

### `ThrowIfUnableToPersist`
- **Type:** `bool`
- **Default:** `true`
- **Purpose:** Determines whether the service should throw an exception if it is unable to persist a task. When set to `true`, it ensures that task persistence failures are explicitly handled, potentially preventing data loss.

### `SetChannelOptions (Overloaded Methods)`
- **Purpose:** Allows configuring `ChannelOptions` either by specifying the capacity directly or by providing a `BoundedChannelOptions` object. Adjusting these options can optimize the task queue's performance and behavior.

### `SetThrowIfUnableToPersist`
- **Purpose:** Enables or disables throwing exceptions when task persistence fails. This can be critical for ensuring data integrity and handling errors appropriately.

### `RegisterTasksFromAssembly`
- **Purpose:** Registers task handlers from a single assembly. This is useful for modular applications where task handlers are defined in different modules.

### `RegisterTasksFromAssemblies`
- **Purpose:** Registers task handlers from multiple assemblies. Ideal for larger applications with distributed task handling logic across various modules or libraries.

## SQL Server Persistence and Logging

### SQL Server Storage
- **Default Schema:** Creates a new schema named `EverTask` by default. This approach avoids adding clutter to the main data schema.
- **Migration Table:** Places the Entity Framework Core migration table in the custom `EverTask` schema.
- **Schema Customization:** Allows specifying a different schema or using `null` to default to the main schema.
- **Migration Handling:** Option to apply database migrations automatically or handle them manually.
 
### Serilog Integration
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

EverTask is complemented by the `EverTask.Abstractions` package, designed for use in Application projects where additional implementations are not required. This allows separation of concerns, keeping your application layer free from infrastructural code.

In your Infrastructure project, where EverTask is added, specify the assembly (or assemblies) containing `IEverTask` requests. This modular approach ensures that the application layer remains clean and focused, while the infrastructure layer handles task execution and management.

## Serialization and deserialization of Requests for Persistence

EverTask uses Newtonsoft.Json for serializing and deserializing task requests, due to its robust support for polymorphism and inheritance, features that are limited in System.Text.Json. It is recommended to use simple objects for task requests, preferably primitives or uncomplicated complex objects, to ensure smooth serialization. In cases where EverTask is unable to serialize a request, it will throw an exception during the `Dispatch` method. This design choice emphasizes reliability in task persistence, ensuring that only serializable tasks are queued for execution.


## Future Developments

| Feature                                          | Description                                                                                                                                                                                |
|--------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Web Dashboard**                                | Implement a simple web dashboard for monitoring tasks.                                                                                                                                     |
| **Delayed and Scheduled Tasks**                  | Executing tasks with a delay (specified via TimeSpan) and scheduled tasks (using cron expressions).                                                                                        |
| **Support for New Storage Options**              | Considering the inclusion of additional storage options like MySql, Postgres, and various DocumentDBs initially supported by EfCore, with the possibility of expanding to other databases. |
| **More examples for using outside ASP.NET Core** | ...and improving all the documentation!                                                                                                                                                    |


&nbsp;

## 🌟 Acknowledgements

Special thanks to **[jbogard](https://github.com/jbogard)** for the **[MediaTr](https://github.com/jbogard/MediatR)** project, providing significant inspiration in the development of key components of this library, especially in the creation of: 

[`TaskDispatcher.cs`](https://github.com/GiampaoloGabba/EverTask/blob/master/src/EverTask/TaskDispatcher.cs)

[`TaskHandlerExecutor.cs`](https://github.com/GiampaoloGabba/EverTask/blob/master/src/EverTask/Handler/TaskHandlerExecutor.cs)

[`TaskHandlerWrapper.cs`](https://github.com/GiampaoloGabba/EverTask/blob/master/src/EverTask/Handler/TaskHandlerWrapper.cs)

[`HandlerRegistrar.cs`](https://github.com/GiampaoloGabba/EverTask/blob/master/src/EverTask/MicrosoftExtensionsDI/HandlerRegistrar.cs)

I have included comments within these files to acknowledge and reference the specific parts of the MediaTr project that inspired them.

Their approach and architecture have been instrumental in shaping the functionality and design of these elements.

This project includes code from [MediatR](https://github.com/jbogard/MediatR), which is licensed under the Apache 2.0 License. The full text of the license can be found in the [LICENSE](LICENSE) file.



