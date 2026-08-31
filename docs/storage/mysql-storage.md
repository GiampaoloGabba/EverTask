---
layout: default
title: MySQL / MariaDB Storage
parent: Storage
nav_order: 6
---

# MySQL / MariaDB Storage

MySQL and MariaDB are open-source relational stores for production. Like the SQL Server and PostgreSQL providers, this provider handles high write concurrency and runs every recovery and cleanup query on the server. EverTask still requires [one active host per store](../scalability.md#horizontal-scaling-multiple-instances); an inactive standby is fine.

The provider is built on [Microting.EntityFrameworkCore.MySql](https://www.nuget.org/packages/Microting.EntityFrameworkCore.MySql), the maintained fork of the (now abandoned) Pomelo provider. It targets **.NET 9 and .NET 10** and is tested against **MariaDB 10.11 LTS**; MySQL 8.0+ is also supported.

## Installation

```bash
dotnet add package EverTask.Storage.MySql
```

## Basic Configuration

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddMySqlStorage("Server=localhost;Database=evertask;User=root;Password=***");
```

## Advanced Configuration

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddMySqlStorage(
    builder.Configuration.GetConnectionString("EverTaskDb")!,
    opt =>
    {
        opt.AutoApplyMigrations = true;                              // Auto-apply EF Core migrations (default: true)
        opt.ServerVersion = new MariaDbServerVersion(new Version(10, 11)); // Optional: skip auto-detection
    });
```

## No Schema Concept

Unlike SQL Server (`EverTask` schema) and PostgreSQL (`evertask` schema), MySQL and MariaDB have **no sub-database schema** (a "schema" is a database, selected by the connection string), so the provider exposes **no `SchemaName` option**: the EverTask tables are created in the connection's database.

The tables are:
- **QueuedTasks**: Main task table
- **StatusAudit**: Task status transition history
- **RunsAudit**: Recurring run execution history
- **TaskExecutionLogs**: Per-execution log entries
- **__EFMigrationsHistory**: EF Core migrations table

## Server Version

`UseMySql` needs to know the server it talks to. By default the provider calls `ServerVersion.AutoDetect(connectionString)`, which opens a short connection at startup to detect MySQL vs MariaDB and the exact version. Set `opt.ServerVersion` explicitly (e.g. `new MariaDbServerVersion(new Version(10, 11))` or `new MySqlServerVersion(new Version(8, 0))`) to skip that probe.

## Migration Management

EverTask applies migrations on startup by default. Disable it to manage them yourself:

```csharp
// Automatic migrations (default)
.AddMySqlStorage(connectionString, opt => opt.AutoApplyMigrations = true);

// Manual migrations
.AddMySqlStorage(connectionString, opt => opt.AutoApplyMigrations = false);
```

With manual migrations you can use the EF Core tools:

```bash
# Apply migrations
dotnet ef database update --project YourProject

# Generate SQL script
dotnet ef migrations script --project YourProject --output migrations.sql
```

## Performance Optimizations

MySQL/MariaDB is a fully relational provider like SQL Server and PostgreSQL (not like SQLite). The provider stores timestamps normalized to UTC and translates every ordering, keyset, and cleanup comparison **server-side**, so it inherits the optimized EF Core base. Recovery and cleanup run as server-side queries rather than materializing rows in memory.

### Run Counter

The run counter **saturates at `int.MaxValue`** instead of overflowing: an unbounded recurring series that reaches that many runs keeps going with the counter frozen at its max. See [Recurring Tasks](../recurring-tasks.md) for the tradeoff.

### GUID Generation

EverTask generates time-ordered GUIDs using the `UUIDNext` v7 family. A v7 GUID's canonical string sorts in temporal order (the timestamp is in the leading bytes), so sequentially generated identifiers keep inserts sequential.

## Connection String Configuration

```csharp
// appsettings.json
{
  "ConnectionStrings": {
    "EverTaskDb": "Server=localhost;Database=evertask;User=evertask;Password=***"
  }
}

// Program.cs
.AddMySqlStorage(builder.Configuration.GetConnectionString("EverTaskDb")!)
```

## Characteristics

- Production-ready
- Open-source (no licensing cost)
- High write concurrency on one active EverTask host per store
- ACID transactions
- Server-side querying for all recovery and cleanup operations
- Targets .NET 9 and .NET 10; tested on MariaDB 10.11 LTS, supports MySQL 8.0+
- Requires a MySQL or MariaDB instance

## Best Practices

### Store Connection String in Configuration

```csharp
// Good: From configuration
.AddMySqlStorage(builder.Configuration.GetConnectionString("EverTaskDb")!)

// Bad: Hardcoded
.AddMySqlStorage("Server=localhost;Database=evertask;User=root;Password=***")
```

### Pin the Server Version in Production

```csharp
// Good: explicit version skips the auto-detect connection at startup
.AddMySqlStorage(connectionString, opt =>
{
    opt.ServerVersion = new MariaDbServerVersion(new Version(10, 11));
})
```

## Testing

The provider is tested end-to-end against a real MariaDB container (Testcontainers `mariadb:10.11`), running the full shared EF Core storage test suite. Running these tests requires Docker (Linux engine).

## When to Use

Use MySQL / MariaDB storage when:
- Running in production at scale
- Need high availability
- Require server-side querying
- Want an open-source database with no licensing cost
- Have existing MySQL or MariaDB infrastructure

Consider alternatives when:
- Running small-scale applications (use SQLite)
- Infrastructure costs are a concern (use SQLite)
- You have existing SQL Server expertise and infrastructure (use SQL Server)

## Next Steps

- **[Audit Configuration](audit-configuration.md)** - Optimize database with audit levels
- **[Best Practices](best-practices.md)** - Storage optimization strategies
- **[PostgreSQL Storage](postgres-storage.md)** - PostgreSQL alternative
- **[SQL Server Storage](sql-server-storage.md)** - Enterprise SQL Server alternative
- **[SQLite Storage](sqlite-storage.md)** - Lightweight alternative
- **[Custom Storage](custom-storage.md)** - Implement your own provider
