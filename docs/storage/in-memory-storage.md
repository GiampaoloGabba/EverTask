---
layout: default
title: In-Memory Storage
parent: Storage
nav_order: 3
---

# In-Memory Storage

In-memory storage is perfect for development and testing when you don't need task persistence.

## Configuration

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddMemoryStorage();
```

## Characteristics

- Zero setup - works out of the box
- Fast performance
- No external dependencies
- Tasks lost on application restart
- Not suitable for production

## Use Cases

### Development Environment

```csharp
// Development environment
#if DEBUG
    .AddMemoryStorage();
#else
    .AddSqlServerStorage(connectionString);
#endif
```

### Integration Tests

```csharp
public class TaskIntegrationTests
{
    [Fact]
    public async Task Should_Execute_Task()
    {
        var services = new ServiceCollection();
        services.AddEverTask(opt => opt.RegisterTasksFromAssembly(GetType().Assembly))
                .AddMemoryStorage();

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<ITaskDispatcher>();

        var taskId = await dispatcher.Dispatch(new TestTask());

        // Assert task execution
    }
}
```

## When to Use

Use in-memory storage when:
- Developing locally
- Running integration tests
- Prototyping features
- Tasks don't need to survive restarts
- You need fast, simple setup

Do NOT use in-memory storage when:
- Running in production
- Tasks must survive application restarts
- You need task history/audit trails
- Tasks are scheduled for future execution
- Multiple application instances need to share tasks

## Next Steps

- **[Storage Overview](overview.md)** - Compare storage providers
- **[SQL Server Storage](sql-server-storage.md)** - Production storage setup
- **[SQLite Storage](sqlite-storage.md)** - Lightweight production storage
- **[Best Practices](best-practices.md)** - Storage selection guidelines
