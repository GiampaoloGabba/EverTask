---
layout: default
title: Multi-Queue Support
parent: Scalability
nav_order: 1
---

# Multi-Queue Support

EverTask supports multiple execution queues so you can isolate workloads, manage priorities, and control resources independently. You might want to keep payment processing separate from bulk email jobs, or run reports on their own queue with limited parallelism to avoid hogging CPU.

## Why Use Multiple Queues?

- **Workload Isolation**: Keep background tasks from blocking critical operations
- **Resource Prioritization**: Give more workers to high-priority queues
- **Capacity Management**: Size different queues based on workload type
- **Performance Optimization**: Tune parallelism to match task characteristics (I/O-bound vs CPU-bound)

## Basic Configuration

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
// Configure the default queue
.ConfigureDefaultQueue(q => q
    .SetMaxDegreeOfParallelism(5)
    .SetChannelCapacity(100)
    .SetFullBehavior(QueueFullBehavior.Wait))

// Add a high-priority queue for critical tasks
.AddQueue("high-priority", q => q
    .SetMaxDegreeOfParallelism(10)
    .SetChannelCapacity(200)
    .SetFullBehavior(QueueFullBehavior.Wait)
    .SetDefaultTimeout(TimeSpan.FromMinutes(5)))

// Add a background queue for CPU-intensive tasks
.AddQueue("background", q => q
    .SetMaxDegreeOfParallelism(2)  // Limit CPU usage
    .SetChannelCapacity(50)
    .SetFullBehavior(QueueFullBehavior.FallbackToDefault))

// Configure the recurring queue (created automatically)
.ConfigureRecurringQueue(q => q
    .SetMaxDegreeOfParallelism(3)
    .SetChannelCapacity(75))

.AddSqlServerStorage(connectionString);
```

## Queue Configuration Options

### MaxDegreeOfParallelism

Controls how many tasks run concurrently in this queue:

```csharp
.AddQueue("api-calls", q => q
    .SetMaxDegreeOfParallelism(20)) // High for I/O-bound tasks

.AddQueue("cpu-intensive", q => q
    .SetMaxDegreeOfParallelism(Environment.ProcessorCount)) // Limited for CPU-bound
```

### ChannelCapacity

Sets the maximum number of tasks that can wait in the queue:

```csharp
.AddQueue("email", q => q
    .SetChannelCapacity(5000)) // Large capacity for bulk operations

.AddQueue("critical", q => q
    .SetChannelCapacity(100)) // Smaller capacity for critical path
```

### QueueFullBehavior

Determines what happens when the queue is full:

```csharp
// Wait: Block until space is available (default)
.AddQueue("important", q => q
    .SetFullBehavior(QueueFullBehavior.Wait))

// FallbackToDefault: Try the default queue if this queue is full
.AddQueue("optional", q => q
    .SetFullBehavior(QueueFullBehavior.FallbackToDefault))

// ThrowException: Throw immediately if full
.AddQueue("strict", q => q
    .SetFullBehavior(QueueFullBehavior.ThrowException))
```

### DefaultRetryPolicy

Sets the default retry policy for all tasks in this queue:

```csharp
.AddQueue("resilient", q => q
    .SetDefaultRetryPolicy(new LinearRetryPolicy(5, TimeSpan.FromSeconds(2))))
```

### DefaultTimeout

Sets the default execution timeout for tasks in this queue:

```csharp
.AddQueue("quick", q => q
    .SetDefaultTimeout(TimeSpan.FromSeconds(30)))
```

## Queue Routing

To send tasks to specific queues, override `QueueName` in your handler:

```csharp
// High-priority queue for critical operations
public class PaymentProcessingHandler : EverTaskHandler<ProcessPaymentTask>
{
    public override string? QueueName => "high-priority";

    public override async Task Handle(ProcessPaymentTask task, CancellationToken cancellationToken)
    {
        // Critical payment processing logic
    }
}

// Background queue for CPU-intensive work
public class ReportGenerationHandler : EverTaskHandler<GenerateReportTask>
{
    public override string? QueueName => "background";

    public override async Task Handle(GenerateReportTask task, CancellationToken cancellationToken)
    {
        // CPU-intensive report generation
    }
}

// Default queue (no QueueName override)
public class EmailHandler : EverTaskHandler<SendEmailTask>
{
    public override async Task Handle(SendEmailTask task, CancellationToken cancellationToken)
    {
        // Regular email sending
    }
}
```

## Automatic Queue Routing

- **Default Queue**: Tasks without a specified `QueueName` go to the "default" queue
- **Recurring Queue**: Recurring tasks go to the "recurring" queue unless explicitly overridden
- **Fallback Behavior**: When a queue is full with `FallbackToDefault`, tasks fall back to the "default" queue

## Real-World Example

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
// Default queue: General background tasks
.ConfigureDefaultQueue(q => q
    .SetMaxDegreeOfParallelism(Environment.ProcessorCount * 2)
    .SetChannelCapacity(1000))

// Critical queue: Payments, orders, user operations
.AddQueue("critical", q => q
    .SetMaxDegreeOfParallelism(20)
    .SetChannelCapacity(500)
    .SetFullBehavior(QueueFullBehavior.Wait)
    .SetDefaultTimeout(TimeSpan.FromMinutes(2))
    .SetDefaultRetryPolicy(new LinearRetryPolicy(5, TimeSpan.FromSeconds(1))))

// Email queue: Bulk email sending
.AddQueue("email", q => q
    .SetMaxDegreeOfParallelism(10)
    .SetChannelCapacity(10000)
    .SetFullBehavior(QueueFullBehavior.FallbackToDefault))

// Reports queue: Heavy processing
.AddQueue("reports", q => q
    .SetMaxDegreeOfParallelism(2)
    .SetChannelCapacity(50)
    .SetDefaultTimeout(TimeSpan.FromMinutes(30)))

// Recurring queue: Scheduled maintenance
.ConfigureRecurringQueue(q => q
    .SetMaxDegreeOfParallelism(4)
    .SetChannelCapacity(100))

.AddSqlServerStorage(connectionString);
```

## Best Practices

1. **Keep Queue Count Reasonable**: Most applications do fine with 3-5 queues. More than that and you're probably over-engineering.
2. **Configure Based on Workload**:
   - I/O operations (API calls, DB queries, file I/O) benefit from higher parallelism
   - CPU-intensive tasks (image processing, calculations) should have lower parallelism
3. **Use Fallback Wisely**: `FallbackToDefault` gives you graceful degradation for non-critical queues
4. **Monitor Queue Metrics**: Track queue depths and processing rates to tune your configuration
5. **Name Queues Clearly**: Use descriptive names like "payments", "email", "reports" instead of generic ones like "queue1", "queue2"

## Next Steps

- [Sharded Scheduler](sharded-scheduler.md) - Scale to extreme loads
- [Task Orchestration](task-orchestration.md) - Chain and coordinate tasks
- [Configuration Reference](configuration-reference.md) - All configuration options
