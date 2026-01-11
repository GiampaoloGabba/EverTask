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
dotnet add package EverTask.SqlServer
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

DbContext pooling is automatically enabled in v2.0+, which reduces the overhead of creating new contexts and improves storage operation performance by 30-50%:

```csharp
.AddSqlServerStorage(connectionString)
```

### Stored Procedures

The SetStatus operation now uses a stored procedure that atomically updates the task status and inserts an audit record in a single database roundtrip. This cuts database calls in half while guaranteeing transactional consistency.

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
