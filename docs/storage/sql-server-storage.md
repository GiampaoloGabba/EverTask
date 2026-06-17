---
layout: default
title: SQL Server Storage
parent: Storage
nav_order: 4
---

# SQL Server Storage

SQL Server provides enterprise-grade storage for production environments.

## Installation

```bash
dotnet add package EverTask.Storage.SqlServer
```

## Basic Configuration

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddSqlServerStorage("Server=localhost;Database=EverTaskDb;Trusted_Connection=True;");
```

## Advanced Configuration

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddSqlServerStorage(
    builder.Configuration.GetConnectionString("EverTaskDb")!,
    opt =>
    {
        opt.SchemaName = "EverTask";          // Custom schema (default: "EverTask")
        opt.AutoApplyMigrations = true;       // Auto-apply EF Core migrations
    });
```

## Schema Configuration

By default, EverTask creates a dedicated schema to keep task tables separate from your main database schema:

```csharp
.AddSqlServerStorage(connectionString, opt =>
{
    // Default behavior: creates "EverTask" schema
    opt.SchemaName = "EverTask";

    // Use main schema (not recommended)
    opt.SchemaName = null;

    // Custom schema
    opt.SchemaName = "Tasks";
});
```

### Schema Contents

The schema contains:
- **QueuedTasks**: Main task table
- **TaskAudit**: Task execution history
- **__EFMigrationsHistory**: EF Core migrations table (also in custom schema)

## Migration Management

EverTask automatically applies migrations on startup by default. You can disable this behavior if you prefer to manage migrations manually:

```csharp
// Automatic migrations (default)
.AddSqlServerStorage(connectionString, opt =>
{
    opt.AutoApplyMigrations = true; // Default behavior
});

// Manual migrations (if preferred)
.AddSqlServerStorage(connectionString, opt =>
{
    opt.AutoApplyMigrations = false;
});
```

If you choose to manage migrations manually, you can use EF Core tools:

```bash
# Apply migrations
dotnet ef database update --project YourProject --context TaskStoreDbContext

# Generate SQL script
dotnet ef migrations script --project YourProject --context TaskStoreDbContext --output migrations.sql
```

## Performance Optimizations (v2.0+)

Version 2.0 introduces significant performance improvements for SQL Server storage.

### DbContext Pooling

DbContext pooling is enabled, so each storage operation rents a context from a pool instead of constructing a fresh one:

```csharp
.AddSqlServerStorage(connectionString)
```

Measured effect (`benchmarks/RESULTS.md`, P-F), and it's provider-agnostic since it's the EF context machinery: per-context allocation drops ~98% (≈6,600 B to ≈104 B) and per-write allocation ~88% on the storage hot path. This is an allocation, GC-pressure, and tail-latency win, not a raw tasks/sec increase. On the end-to-end durable path (measured on PostgreSQL, the representative durable provider on this hardware) it cut per-task allocation ~71% and roughly halved the p999 latency tail, while throughput stayed bound by the database round-trip. The same pooling applies to SQL Server; an end-to-end SQL Server figure is pending a measurement on real hardware (the Docker/WSL2 numbers are I/O-penalized).

### Stored Procedures

The SetStatus operation uses a stored procedure that performs the status update and the audit-record insert in a **single round-trip and a single transaction**, instead of two statements, while guaranteeing transactional consistency.

## Connection String Configuration

```csharp
// appsettings.json
{
  "ConnectionStrings": {
    "EverTaskDb": "Server=localhost;Database=EverTaskDb;User Id=evertask;Password=***;TrustServerCertificate=True"
  }
}

// Program.cs
.AddSqlServerStorage(builder.Configuration.GetConnectionString("EverTaskDb")!)
```

## Characteristics

- Production-ready
- Highly scalable
- ACID transactions
- Stored procedures for performance
- Rich querying capabilities
- Requires SQL Server instance
- Additional infrastructure cost

## Best Practices

### Use Dedicated Schema

```csharp
// Good: Use dedicated schema
.AddSqlServerStorage(connectionString, opt =>
{
    opt.SchemaName = "EverTask";
})

// Bad: Pollute main schema
.AddSqlServerStorage(connectionString, opt =>
{
    opt.SchemaName = null;
})
```

### Store Connection String in Configuration

```csharp
// Good: From configuration
.AddSqlServerStorage(builder.Configuration.GetConnectionString("EverTaskDb")!)

// Bad: Hardcoded
.AddSqlServerStorage("Server=localhost;Database=EverTaskDb;...")
```

## When to Use

Use SQL Server storage when:
- Running in production at scale
- Need high availability
- Require robust querying
- Have existing SQL Server infrastructure
- Need enterprise-grade reliability

Consider alternatives when:
- Running small-scale applications (use SQLite)
- Infrastructure costs are a concern (use SQLite)
- Don't have SQL Server expertise (use SQLite or In-Memory)

## Next Steps

- **[Audit Configuration](audit-configuration.md)** - Optimize database with audit levels
- **[Best Practices](best-practices.md)** - Storage optimization strategies
- **[SQLite Storage](sqlite-storage.md)** - Lightweight alternative
- **[Custom Storage](custom-storage.md)** - Implement your own provider
