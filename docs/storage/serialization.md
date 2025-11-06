---
layout: default
title: Serialization
parent: Storage
nav_order: 7
---

# Serialization

EverTask uses **Newtonsoft.Json** for task serialization because it handles polymorphism and inheritance well.

## Serialization Best Practices

### Good Task Designs

```csharp
// Simple primitives
public record GoodTask1(int Id, string Name, DateTime Date) : IEverTask;

// Simple collections
public record GoodTask2(List<int> Ids, Dictionary<string, string> Metadata) : IEverTask;

// Simple nested objects
public record Address(string Street, string City);
public record GoodTask3(string Name, Address Address) : IEverTask;
```

### Problematic Task Designs

```csharp
// Circular references
public class BadTask1 : IEverTask
{
    public BadTask1? Parent { get; set; }
    public List<BadTask1> Children { get; set; }
}

// Non-serializable types
public record BadTask2(DbContext Context, ILogger Logger) : IEverTask;

// Streams or delegates
public record BadTask3(Stream Data, Func<int> Callback) : IEverTask;

// Deep object graphs
public record BadTask4(ComplexObject WithManyNestedLevels) : IEverTask;
```

## Design Guidelines

### Use Primitives and Simple Types

**Good:**
```csharp
public record ProcessOrderTask(
    int OrderId,
    string CustomerEmail,
    List<int> ItemIds) : IEverTask;
```

**Bad:**
```csharp
public record ProcessOrderTask(
    Order Order, // DbContext-tracked entity
    IOrderService OrderService, // Service dependency
    Func<bool> ValidationCallback) : IEverTask; // Delegate
```

### Use IDs Instead of Entities

**Good:**
```csharp
public record SendEmailTask(
    Guid UserId,
    string EmailTemplate) : IEverTask;

public class SendEmailHandler : EverTaskHandler<SendEmailTask>
{
    private readonly UserRepository _userRepository;

    public override async Task Handle(SendEmailTask task, CancellationToken ct)
    {
        // Load entity from database inside handler
        var user = await _userRepository.GetByIdAsync(task.UserId, ct);
        await SendEmail(user.Email, task.EmailTemplate);
    }
}
```

**Bad:**
```csharp
public record SendEmailTask(
    User User, // Don't pass entities
    string EmailTemplate) : IEverTask;
```

### Avoid Service Dependencies in Tasks

**Good:**
```csharp
public record SendEmailTask(string ToEmail, string Subject) : IEverTask;

public class SendEmailHandler : EverTaskHandler<SendEmailTask>
{
    private readonly IEmailService _emailService; // Inject in handler

    public override async Task Handle(SendEmailTask task, CancellationToken ct)
    {
        await _emailService.SendAsync(task.ToEmail, task.Subject, ct);
    }
}
```

**Bad:**
```csharp
public record SendEmailTask(
    string ToEmail,
    IEmailService EmailService) : IEverTask; // Don't pass services
```

### Keep Tasks Simple

Tasks should be simple data containers. Complex logic belongs in handlers, not tasks.

**Good:**
```csharp
// Simple data container
public record ProcessPaymentTask(
    Guid OrderId,
    decimal Amount,
    string Currency) : IEverTask;
```

**Bad:**
```csharp
// Complex behavior in task
public record ProcessPaymentTask(
    Guid OrderId,
    decimal Amount,
    string Currency) : IEverTask
{
    public decimal CalculateTax() => Amount * 0.1m; // Don't add logic to tasks
    public bool IsValid() => Amount > 0; // Don't add logic to tasks
}
```

## Handling Serialization Failures

```csharp
try
{
    await dispatcher.Dispatch(new MyTask(data));
}
catch (JsonSerializationException ex)
{
    _logger.LogError(ex, "Failed to serialize task. Ensure task contains only serializable types.");
    // Handle error - simplify task data or use different approach
}
```

## Custom Serialization Settings

EverTask handles serialization internally. We don't recommend customizing this unless you have a specific need and understand the implications.

## Common Serialization Issues

### Issue: Circular References

**Problem:**
```csharp
public class BadTask : IEverTask
{
    public BadTask? Parent { get; set; }
    public List<BadTask> Children { get; set; }
}
```

**Solution:**
Use IDs to represent relationships:
```csharp
public record GoodTask(
    Guid? ParentId,
    List<Guid> ChildIds) : IEverTask;
```

### Issue: Non-Serializable Types

**Problem:**
```csharp
public record BadTask(
    DbContext Context,
    ILogger Logger,
    Stream Data) : IEverTask;
```

**Solution:**
Pass only serializable data:
```csharp
public record GoodTask(
    byte[] Data,
    string FileName) : IEverTask;

public class GoodTaskHandler : EverTaskHandler<GoodTask>
{
    private readonly IDbContextFactory<MyDbContext> _contextFactory;
    private readonly ILogger<GoodTaskHandler> _logger;

    // Inject dependencies in handler, not task
    public override async Task Handle(GoodTask task, CancellationToken ct)
    {
        using var context = await _contextFactory.CreateDbContextAsync(ct);
        _logger.LogInformation("Processing {FileName}", task.FileName);
        // Process task.Data
    }
}
```

### Issue: Entity Framework Entities

**Problem:**
```csharp
public record BadTask(Order Order) : IEverTask; // EF entity with navigation properties
```

**Solution:**
Pass entity ID and load in handler:
```csharp
public record GoodTask(int OrderId) : IEverTask;

public class GoodTaskHandler : EverTaskHandler<GoodTask>
{
    private readonly MyDbContext _dbContext;

    public override async Task Handle(GoodTask task, CancellationToken ct)
    {
        var order = await _dbContext.Orders
            .Include(o => o.Items)
            .FirstAsync(o => o.Id == task.OrderId, ct);

        // Process order
    }
}
```

## Testing Serialization

Test that your tasks serialize correctly:

```csharp
[Fact]
public void Task_Should_Serialize_And_Deserialize()
{
    var originalTask = new MyTask(123, "test", DateTime.UtcNow);

    var json = JsonConvert.SerializeObject(originalTask);
    var deserializedTask = JsonConvert.DeserializeObject<MyTask>(json);

    deserializedTask.ShouldNotBeNull();
    deserializedTask.Id.ShouldBe(originalTask.Id);
    deserializedTask.Name.ShouldBe(originalTask.Name);
}
```

## Next Steps

- **[Best Practices](best-practices.md)** - Overall storage best practices
- **[Custom Storage](custom-storage.md)** - Implement custom serialization logic
- **[Task Creation](../task-creation.md)** - Learn about task design patterns
