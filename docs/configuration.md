---
layout: default
title: Configuration
nav_order: 9
has_children: true
---

# Configuration

EverTask provides extensive configuration options to control task execution, queue management, retry policies, timeouts, and more.

## Quick Access

### 📖 [Configuration Reference](configuration-reference.md)
Complete reference documentation for all EverTask configuration options with detailed explanations, examples, and best practices.

**Use this when:**
- Setting up EverTask for the first time
- Understanding the purpose and impact of specific options
- Learning about advanced configuration scenarios
- Troubleshooting configuration issues

### ⚡ [Configuration Cheatsheet](configuration-cheatsheet.md)
Quick reference guide with all configuration options in a concise, scannable format.

**Use this when:**
- You already know what to configure
- Looking up syntax quickly
- Need a reminder of available options
- Want to see all options at a glance

## Common Configuration Scenarios

### Minimal Setup
```csharp
services.AddEverTask(options =>
{
    options.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddMemoryStorage();
```

### Production Setup
```csharp
services.AddEverTask(options =>
{
    options.MaxConcurrency = 10;
    options.DefaultRetryPolicy = new RetryPolicy
    {
        MaxAttempts = 3,
        DelayMilliseconds = 1000,
        BackoffMultiplier = 2.0
    };
    options.DefaultTimeoutMilliseconds = 30000;
})
.AddSqlServerStorage(connectionString);
```

### High-Throughput Setup
```csharp
services.AddEverTask(options =>
{
    options.MaxConcurrency = 50;
    options.QueueCapacity = 1000;
    options.EnableMultiQueue = true;
    options.RegisterQueue("critical", priority: 1, maxConcurrency: 20);
    options.RegisterQueue("standard", priority: 5, maxConcurrency: 20);
    options.RegisterQueue("background", priority: 10, maxConcurrency: 10);
})
.AddSqlServerStorage(connectionString);
```

## Next Steps

- **[Configuration Reference](configuration-reference.md)** - Complete documentation
- **[Configuration Cheatsheet](configuration-cheatsheet.md)** - Quick reference
- **[Storage](storage.md)** - Configure persistence
- **[Resilience](resilience.md)** - Retry policies and error handling
