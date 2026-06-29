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

EverTask is a .NET background task execution library focused on persistence and resilience. Use it to process work in the background, run scheduled jobs, and build task pipelines that survive failures and restarts.

## Quick Example

```csharp
// 1. Register in Program.cs
builder.Services.AddEverTask(opt =>
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly)
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
await _dispatcher.Dispatch(new SendEmailTask("user@example.com", "Hello!"));
```

With those four steps in place, EverTask persists the task, executes it in the background, and retries it if the handler throws. See [Getting Started](getting-started.md) for the details.

## Table of Contents

### Getting Started
- **[Getting Started](getting-started.md)** - Get EverTask running in your app
  - [Task Creation](task-creation.md) - Create and configure tasks and handlers
  - [Task Dispatching](task-dispatching.md) - Execute tasks immediately or on a schedule

### Core Concepts
- **[Storage](storage.md)** - Choose your persistence layer (SQL Server, PostgreSQL, MySQL/MariaDB, SQLite, or in-memory)
  - [Overview](storage/overview.md) - Picking a provider
  - [Audit Configuration](storage/audit-configuration.md) - Audit levels and retention
  - [In-Memory Storage](storage/in-memory-storage.md) - Zero-infra storage for dev and tests
  - [SQL Server Storage](storage/sql-server-storage.md) - Setup and schema
  - [PostgreSQL Storage](storage/postgres-storage.md) - Setup and schema
  - [MySQL / MariaDB Storage](storage/mysql-storage.md) - Setup and connection strings
  - [SQLite Storage](storage/sqlite-storage.md) - Setup and connection strings
  - [Custom Storage](storage/custom-storage.md) - Implement ITaskStorage
  - [Serialization](storage/serialization.md) - System.Text.Json payload contract
  - [Best Practices](storage/best-practices.md) - Patterns and pitfalls
- **[Configuration](configuration.md)** - Configure EverTask
  - [Configuration Reference](configuration-reference.md) - Complete configuration options
  - [Configuration Cheatsheet](configuration-cheatsheet.md) - Quick reference guide

### Advanced Topics
- **[Recurring Tasks](recurring-tasks.md)** - Schedule jobs with the fluent API or cron expressions
  - [Overview](recurring-tasks/overview.md) - Concepts and when to use recurring tasks
  - [Fluent Scheduling API](recurring-tasks/fluent-api.md) - Build schedules by minute, hour, day, week, or month
  - [Cron Expressions](recurring-tasks/cron-expressions.md) - 5- and 6-field cron schedules
  - [Idempotent Registration](recurring-tasks/idempotent-registration.md) - Register recurring tasks safely on every startup
  - [Managing Tasks](recurring-tasks/managing-tasks.md) - Inspect, cancel, and reschedule recurring tasks
  - [Best Practices](recurring-tasks/best-practices.md) - Patterns and pitfalls
- **[Resilience](resilience.md)** - Handle failures with retry policies and timeouts
  - [Overview](resilience/overview.md) - Failure handling at a glance
  - [Retry Policies](resilience/retry-policies.md) - Linear retry, custom policies, Polly
  - [Exception Filtering](resilience/exception-filtering.md) - Whitelist, blacklist, and predicate filtering
  - [Retry Callbacks](resilience/retry-callbacks.md) - React to each retry attempt
  - [Timeout Management](resilience/timeout-management.md) - Per-handler, per-queue, and global timeouts
  - [Cancellation Tokens](resilience/cancellation-tokens.md) - Cooperative cancellation in handlers
  - [Graceful Shutdown](resilience/graceful-shutdown.md) - What happens to running tasks on stop
  - [Error Observation](resilience/error-observation.md) - Observe and record failures
  - [Best Practices](resilience/best-practices.md) - Patterns and pitfalls
- **[Scalability](scalability.md)** - Performance and scalability features
  - [Multi-Queue Support](multi-queue.md) - Workload isolation by priority or domain
  - [Sharded Scheduler](sharded-scheduler.md) - Lower scheduler lock contention under high Schedule() call rates
  - [Keyed Rate Limiting](rate-limiting.md) - Per-tenant/per-resource throttling against external API limits
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

### Tooling
- [Agent Skill](agent-skill.md) - AI-assisted integration: install the skill and let an agent wire up EverTask (one-step on Claude Code)

## Quick Links

- [GitHub Repository](https://github.com/GiampaoloGabba/EverTask)
- [NuGet Package](https://www.nuget.org/packages/EverTask)
- [Release Notes](https://github.com/GiampaoloGabba/EverTask/releases)

## Support

For questions, bug reports, or contributions, use the [GitHub Issues](https://github.com/GiampaoloGabba/EverTask/issues) page.
