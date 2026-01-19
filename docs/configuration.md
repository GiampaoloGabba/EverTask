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
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddMemoryStorage();
```

### Production Setup
```csharp
builder.Services.AddEverTask(opt =>
{
    opt.SetMaxDegreeOfParallelism(10)
       .SetDefaultRetryPolicy(new LinearRetryPolicy(3, TimeSpan.FromSeconds(1)))
       .SetDefaultTimeout(TimeSpan.FromSeconds(30))
       .RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddSqlServerStorage(connectionString);
```

### High-Throughput Setup
```csharp
builder.Services.AddEverTask(opt =>
{
    opt.SetMaxDegreeOfParallelism(50)
       .SetChannelOptions(5000)
       .RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.ConfigureDefaultQueue(q => q
    .SetMaxDegreeOfParallelism(20)
    .SetChannelCapacity(500))
.AddQueue("critical", q => q
    .SetMaxDegreeOfParallelism(20)
    .SetChannelCapacity(500))
.AddQueue("background", q => q
    .SetMaxDegreeOfParallelism(10)
    .SetChannelCapacity(200))
.AddSqlServerStorage(connectionString);
```

## Next Steps

- **[Configuration Reference](configuration-reference.md)** - Complete documentation
- **[Configuration Cheatsheet](configuration-cheatsheet.md)** - Quick reference
- **[Storage](storage.md)** - Configure persistence
- **[Resilience](resilience.md)** - Retry policies and error handling
