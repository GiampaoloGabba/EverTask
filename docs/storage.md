---
layout: default
title: Storage
nav_order: 6
has_children: true
---

# Storage Configuration

EverTask supports multiple storage providers for task persistence. This guide covers all available options and how to implement custom storage.

## Overview

Storage providers persist tasks and their state, enabling task recovery after application restarts, task history tracking, audit trails, and scheduled/recurring task management.

## Storage Topics

### [Storage Overview](storage/overview.md)
Learn about the available storage providers, their characteristics, and how to choose the right one for your use case.

### [Audit Configuration](storage/audit-configuration.md)
Configure audit trail levels to control database bloat from high-frequency tasks. Learn about Full, Minimal, ErrorsOnly, and None audit levels.

### [In-Memory Storage](storage/in-memory-storage.md)
Fast, zero-setup storage perfect for development and testing. Tasks are lost on application restart.

### [SQL Server Storage](storage/sql-server-storage.md)
Enterprise-grade storage for production environments with DbContext pooling, stored procedures, and schema management.

### [SQLite Storage](storage/sqlite-storage.md)
Lightweight, file-based storage ideal for small-scale production and single-server deployments.

### [Custom Storage](storage/custom-storage.md)
Learn how to implement custom storage providers for Redis, MongoDB, PostgreSQL, or any other database.

### [Serialization](storage/serialization.md)
Best practices for designing serializable tasks and handling serialization with Newtonsoft.Json.

### [Best Practices](storage/best-practices.md)
Storage selection guidelines, connection string management, migration strategies, and cleanup tasks.

## Quick Start

### Development Setup
```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddMemoryStorage();
```

### Production Setup (SQL Server)
```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddSqlServerStorage(
    builder.Configuration.GetConnectionString("EverTaskDb")!,
    opt =>
    {
        opt.SchemaName = "EverTask";
        opt.AutoApplyMigrations = true;
    });
```

### Production Setup (SQLite)
```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddSqliteStorage("Data Source=evertask.db");
```

## Next Steps

- **[Configuration Reference](configuration-reference.md)** - All storage configuration options
- **[Architecture](architecture.md)** - How storage integrates with EverTask
- **[Getting Started](getting-started.md)** - Setup guide with storage configuration
- **[Monitoring](monitoring.md)** - Monitor storage performance
