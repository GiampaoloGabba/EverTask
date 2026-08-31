---
layout: default
title: PostgreSQL Storage
parent: Storage
nav_order: 5
---

# PostgreSQL Storage

PostgreSQL is an open-source relational store for production. Like the SQL Server provider, it handles high write concurrency and runs every recovery and cleanup query on the server. EverTask still requires [one active host per store](../scalability.md#horizontal-scaling-multiple-instances); an inactive standby is fine.

## Installation

```bash
dotnet add package EverTask.Storage.Postgres
```

## Basic Configuration

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddPostgresStorage("Host=localhost;Database=evertask;Username=postgres;Password=***");
```

## Advanced Configuration

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddPostgresStorage(
    builder.Configuration.GetConnectionString("EverTaskDb")!,
    opt =>
    {
        opt.SchemaName = "evertask";          // Custom schema (default: "evertask", MUST be lowercase)
        opt.AutoApplyMigrations = true;       // Auto-apply EF Core migrations
    });
```

## Schema Configuration

By default, EverTask creates a dedicated schema to keep task tables separate from your main database schema:

```csharp
.AddPostgresStorage(connectionString, opt =>
{
    // Default behavior: creates "evertask" schema
    opt.SchemaName = "evertask";

    // Use the public schema (not recommended)
    opt.SchemaName = null;

    // Custom schema (lowercase only)
    opt.SchemaName = "tasks";
});
```

### Schema Name Must Be Lowercase

The schema name **must be lowercase**. PostgreSQL folds **unquoted** identifiers to lowercase, but EF Core / Npgsql **always** double-quotes the identifiers it generates. A mixed-case value such as `"EverTask"` therefore becomes permanently case-sensitive: every hand-written `psql` query or `search_path` entry would have to quote it exactly the same way. Stick to lowercase values matching `^[a-z_][a-z0-9_]*$` (the default `"evertask"` is safe).

### Schema Contents

The schema contains:
- **QueuedTasks**: Main task table
- **StatusAudit**: Task status transition history
- **RunsAudit**: Recurring run execution history
- **__EFMigrationsHistory**: EF Core migrations table (also in the custom schema)

## Schema-Aware Migrations

The schema is **runtime-configurable**, with full parity with the SQL Server provider. EverTask injects the configured schema into the migration at runtime, so the same migration applies cleanly to any schema you select via `SchemaName`. The migrations history table is created inside the same schema (not in `public`).

## Migration Management

EverTask automatically applies migrations on startup by default. You can disable this behavior if you prefer to manage migrations manually:

```csharp
// Automatic migrations (default)
.AddPostgresStorage(connectionString, opt =>
{
    opt.AutoApplyMigrations = true; // Default behavior
});

// Manual migrations (if preferred)
.AddPostgresStorage(connectionString, opt =>
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

## Performance Optimizations

PostgreSQL is a fully relational provider like SQL Server (not like SQLite). Npgsql maps `DateTimeOffset` to `timestamptz` and translates every ordering, keyset, and cleanup comparison **server-side**, so the provider inherits the optimized EF Core base with **no client-side overrides**. Recovery and cleanup run entirely on the server, with no in-memory filtering.

### Run Counter

The run counter **saturates at `int.MaxValue`** instead of overflowing: an unbounded recurring series that reaches that many runs keeps going with the counter frozen at its max. See [Recurring Tasks](../recurring-tasks.md) for the tradeoff.

### GUID Generation

EverTask generates time-ordered GUIDs using the `UUIDNext` PostgreSQL (v7) family. PostgreSQL sorts `uuid` values byte-wise, so sequentially generated identifiers stay in temporal order and inserts remain sequential.

## Connection String Configuration

```csharp
// appsettings.json
{
  "ConnectionStrings": {
    "EverTaskDb": "Host=localhost;Database=evertask;Username=evertask;Password=***"
  }
}

// Program.cs
.AddPostgresStorage(builder.Configuration.GetConnectionString("EverTaskDb")!)
```

## Characteristics

- Production-ready
- Open-source (no licensing cost)
- High write concurrency on one active EverTask host per store
- ACID transactions
- Server-side querying for all recovery and cleanup operations
- Requires a PostgreSQL instance

## Best Practices

### Use a Dedicated Lowercase Schema

```csharp
// Good: Use a dedicated, lowercase schema
.AddPostgresStorage(connectionString, opt =>
{
    opt.SchemaName = "evertask";
})

// Bad: Mixed case (becomes permanently case-sensitive)
.AddPostgresStorage(connectionString, opt =>
{
    opt.SchemaName = "EverTask";
})

// Bad: Pollute the public schema
.AddPostgresStorage(connectionString, opt =>
{
    opt.SchemaName = null;
})
```

### Store Connection String in Configuration

```csharp
// Good: From configuration
.AddPostgresStorage(builder.Configuration.GetConnectionString("EverTaskDb")!)

// Bad: Hardcoded
.AddPostgresStorage("Host=localhost;Database=evertask;Username=postgres;Password=***")
```

## Testing

The provider is tested end-to-end against a real PostgreSQL container (Testcontainers `postgres:16-alpine`), running the full shared EF Core storage test suite with zero client-side overrides. Running these tests requires Docker (Linux engine).

## When to Use

Use PostgreSQL storage when:
- Running in production at scale
- Need high availability
- Require server-side querying
- Want an open-source database with no licensing cost
- Have existing PostgreSQL infrastructure
- Need high reliability

Consider alternatives when:
- Running small-scale applications (use SQLite)
- Infrastructure costs are a concern (use SQLite)
- You have existing SQL Server expertise and infrastructure (use SQL Server)

## Next Steps

- **[Audit Configuration](audit-configuration.md)** - Optimize database with audit levels
- **[Best Practices](best-practices.md)** - Storage optimization strategies
- **[SQL Server Storage](sql-server-storage.md)** - Enterprise SQL Server alternative
- **[MySQL / MariaDB Storage](mysql-storage.md)** - MySQL/MariaDB alternative
- **[SQLite Storage](sqlite-storage.md)** - Lightweight alternative
- **[Custom Storage](custom-storage.md)** - Implement your own provider
