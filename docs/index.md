---
layout: home
title: Home
nav_order: 1
---

<div style="text-align: center; margin: 2rem 0;">
  <img src="assets/logo-icon.png" alt="EverTask" width="120" height="120" style="margin-bottom: 1rem;">
  <h1 style="margin: 0;">EverTask Documentation</h1>
</div>

<p align="center">
  <a href="https://www.nuget.org/packages/EverTask"><img src="https://img.shields.io/nuget/v/EverTask.svg?style=flat-square&label=nuget" alt="NuGet"></a>
  <a href="https://www.nuget.org/packages/EverTask"><img src="https://img.shields.io/nuget/dt/EverTask.svg?style=flat-square" alt="Downloads"></a>
  <a href="https://github.com/GiampaoloGabba/EverTask/blob/master/LICENSE"><img src="https://img.shields.io/github/license/GiampaoloGabba/EverTask.svg?style=flat-square" alt="License"></a>
</p>

---

EverTask is a .NET background task execution library built for **persistence** and **resilience**. If you need to process work in the background, handle scheduled jobs, or build fault-tolerant task pipelines, you're in the right place.

## Quick Example

```csharp
// 1. Register in Program.cs
builder.Services.AddEverTask(options =>
    options.RegisterTasksFromAssemblyContaining<SendEmailTask>()
).AddSqlServerStorage(connectionString);

// 2. Define your task
public record SendEmailTask(string To, string Subject) : IEverTask;

// 3. Define your handler
public class SendEmailHandler : EverTaskHandler<SendEmailTask>
{
    public override async Task Handle(SendEmailTask task, CancellationToken ct)
    {
        await _emailService.SendAsync(task.To, task.Subject);
    }
}

// 4. Dispatch anywhere
await _dispatcher.DispatchAsync(new SendEmailTask("user@example.com", "Hello!"));
```

**That's it!** EverTask persists the task, handles retries on failure, and executes it in the background. Learn more in [Getting Started](getting-started.md).

## Table of Contents

### Getting Started
- **[Getting Started](getting-started.md)** - Get EverTask running in your app
  - [Task Creation](task-creation.md) - Create and configure tasks and handlers
  - [Task Dispatching](task-dispatching.md) - Execute tasks immediately or on a schedule

### Core Concepts
- [Storage](storage.md) - Choose your persistence layer (SQL Server, SQLite, or in-memory)
- **[Configuration](configuration.md)** - Configure EverTask
  - [Configuration Reference](configuration-reference.md) - Complete configuration options
  - [Configuration Cheatsheet](configuration-cheatsheet.md) - Quick reference guide

### Advanced Topics
- [Recurring Tasks](recurring-tasks.md) - Schedule jobs with the fluent API or cron expressions
- [Resilience](resilience.md) - Handle failures with retry policies and timeouts
- **[Scalability](scalability.md)** - Performance and scalability features
  - [Multi-Queue Support](multi-queue.md) - Workload isolation by priority or domain
  - [Sharded Scheduler](sharded-scheduler.md) - Extreme load support (>10k tasks/sec)
- **[Monitoring](monitoring.md)** - Complete monitoring guide
  - [Custom Event Monitoring](monitoring-events.md) - Event system and integrations
  - [Monitoring Dashboard](monitoring-dashboard.md) - Web dashboard and REST API
  - [API Reference](monitoring-api-reference.md) - Complete API documentation
  - [Dashboard UI Guide](monitoring-dashboard-ui.md) - UI features and screenshots
  - [Task Execution Logs](monitoring-logs.md) - Log capture and persistence
- **[Workflows](advanced-features.md)** - Coordinate complex workflows
  - [Task Orchestration](task-orchestration.md) - Continuations, cancellation, rescheduling
  - [Custom Workflows](custom-workflows.md) - Build complex task pipelines
- [Architecture](architecture.md) - How EverTask works under the hood

## Quick Links

- [GitHub Repository](https://github.com/GiampaoloGabba/EverTask)
- [NuGet Package](https://www.nuget.org/packages/EverTask)
- [Release Notes](https://github.com/GiampaoloGabba/EverTask/releases)

## Support

Questions? Found a bug? Want to contribute? Head over to our [GitHub Issues](https://github.com/GiampaoloGabba/EverTask/issues) page.
