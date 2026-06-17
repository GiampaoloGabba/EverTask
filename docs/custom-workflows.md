---
layout: default
title: Custom Workflows
parent: Workflows
nav_order: 2
---

# Custom Workflows

Combine continuations, rescheduling, and conditional logic to build sophisticated workflows that orchestrate complex business processes.

## Workflow Orchestrator Pattern

The lifecycle callbacks receive only the task ID, so the orchestrator captures the request into private fields during `Handle` and reads them back in `OnCompleted` and `OnError`:

```csharp
public class WorkflowOrchestrator : EverTaskHandler<WorkflowTask>
{
    private readonly ITaskDispatcher _dispatcher;
    private Guid _workflowId;
    private WorkflowStage _stage;

    public override async Task Handle(WorkflowTask task, CancellationToken cancellationToken)
    {
        _workflowId = task.WorkflowId;
        _stage = task.Stage;

        // Execute current stage
        await ExecuteStageAsync(task.Stage, cancellationToken);
    }

    public override async ValueTask OnCompleted(Guid taskId)
    {
        switch (_stage)
        {
            case WorkflowStage.Validation:
                // Move to payment stage
                await _dispatcher.Dispatch(new WorkflowTask(
                    _workflowId,
                    WorkflowStage.Payment));
                break;

            case WorkflowStage.Payment:
                // Wait 1 hour before fulfillment
                await _dispatcher.Dispatch(
                    new WorkflowTask(_workflowId, WorkflowStage.Fulfillment),
                    TimeSpan.FromHours(1));
                break;

            case WorkflowStage.Fulfillment:
                // Final stage - send confirmation
                await _dispatcher.Dispatch(new SendConfirmationTask(_workflowId));
                break;
        }
    }

    public override async ValueTask OnError(Guid taskId, Exception? exception, string? message)
    {
        // Rollback workflow on any stage failure
        await _dispatcher.Dispatch(new RollbackWorkflowTask(_workflowId, _stage));
    }
}
```

## Order Processing Workflow Example

```csharp
public record WorkflowTask(Guid WorkflowId, WorkflowStage Stage) : IEverTask;

public enum WorkflowStage
{
    Validation,
    Payment,
    Inventory,
    Fulfillment,
    Notification
}

// Stage implementations
public class ValidationStageHandler : EverTaskHandler<ValidationStageTask>
{
    private Guid _orderId;
    private Guid _workflowId;

    public override async Task Handle(ValidationStageTask task, CancellationToken ct)
    {
        _orderId = task.OrderId;
        _workflowId = task.WorkflowId;

        // Validate order details
        await ValidateOrderAsync(task.OrderId, ct);
    }

    public override async ValueTask OnCompleted(Guid taskId)
    {
        // Progress to payment
        await _dispatcher.Dispatch(new WorkflowTask(
            _workflowId,
            WorkflowStage.Payment));
    }

    public override async ValueTask OnError(Guid taskId, Exception? ex, string? msg)
    {
        // Notify customer of validation failure
        await _dispatcher.Dispatch(new SendValidationErrorEmailTask(_orderId));
    }
}

public class PaymentStageHandler : EverTaskHandler<PaymentStageTask>
{
    private Guid _orderId;
    private Guid _workflowId;

    public override async Task Handle(PaymentStageTask task, CancellationToken ct)
    {
        _orderId = task.OrderId;
        _workflowId = task.WorkflowId;

        // Process payment
        await ChargePaymentMethodAsync(task.OrderId, ct);
    }

    public override async ValueTask OnCompleted(Guid taskId)
    {
        // Progress to inventory
        await _dispatcher.Dispatch(new WorkflowTask(
            _workflowId,
            WorkflowStage.Inventory));
    }

    public override async ValueTask OnError(Guid taskId, Exception? ex, string? msg)
    {
        // Notify customer and cancel order
        await _dispatcher.Dispatch(new SendPaymentFailedEmailTask(_orderId));
        await _dispatcher.Dispatch(new CancelOrderTask(_orderId));
    }
}
```

## Saga Pattern Implementation

```csharp
public class OrderSagaOrchestrator : EverTaskHandler<OrderSagaTask>
{
    private readonly ITaskDispatcher _dispatcher;
    private readonly List<Guid> _completedSteps = new();
    private Guid _orderId;
    private SagaStep _step;

    public override async Task Handle(OrderSagaTask task, CancellationToken ct)
    {
        _orderId = task.OrderId;
        _step = task.Step;

        switch (task.Step)
        {
            case SagaStep.ReserveInventory:
                await ReserveInventoryAsync(task.OrderId, ct);
                break;

            case SagaStep.ChargePayment:
                await ChargePaymentAsync(task.OrderId, ct);
                break;

            case SagaStep.CreateShipment:
                await CreateShipmentAsync(task.OrderId, ct);
                break;
        }

        _completedSteps.Add(task.Step);
    }

    public override async ValueTask OnCompleted(Guid taskId)
    {
        var nextStep = GetNextStep(_step);
        if (nextStep.HasValue)
        {
            await _dispatcher.Dispatch(new OrderSagaTask(
                _orderId,
                nextStep.Value,
                _completedSteps));
        }
    }

    public override async ValueTask OnError(Guid taskId, Exception? ex, string? msg)
    {
        // Compensate all completed steps in reverse order
        foreach (var step in _completedSteps.Reverse())
        {
            await CompensateStep(step, _orderId);
        }

        // Notify failure
        await _dispatcher.Dispatch(new OrderFailedTask(_orderId));
    }

    private async Task CompensateStep(SagaStep step, Guid orderId)
    {
        switch (step)
        {
            case SagaStep.ReserveInventory:
                await _dispatcher.Dispatch(new ReleaseInventoryTask(orderId));
                break;

            case SagaStep.ChargePayment:
                await _dispatcher.Dispatch(new RefundPaymentTask(orderId));
                break;

            case SagaStep.CreateShipment:
                await _dispatcher.Dispatch(new CancelShipmentTask(orderId));
                break;
        }
    }
}
```

## State Machine Workflow

```csharp
public class OrderStateMachine : EverTaskHandler<OrderStateTask>
{
    private readonly ITaskDispatcher _dispatcher;
    private Guid _orderId;
    private OrderState _currentState;
    private OrderEvent? _event;

    public override async Task Handle(OrderStateTask task, CancellationToken ct)
    {
        _orderId = task.OrderId;
        _currentState = task.CurrentState;
        _event = task.Event;

        // Execute action for current state
        await ExecuteStateAction(task.CurrentState, task.OrderId, ct);
    }

    public override async ValueTask OnCompleted(Guid taskId)
    {
        var nextState = GetNextState(_currentState, _event);

        if (nextState == OrderState.Completed)
        {
            // Workflow complete
            await _dispatcher.Dispatch(new OrderCompletedTask(_orderId));
        }
        else if (nextState != null)
        {
            // Transition to next state
            await _dispatcher.Dispatch(new OrderStateTask(
                _orderId,
                nextState.Value,
                null));
        }
    }

    private OrderState? GetNextState(OrderState current, OrderEvent? eventType)
    {
        return (current, eventType) switch
        {
            (OrderState.Created, OrderEvent.PaymentReceived) => OrderState.Paid,
            (OrderState.Paid, OrderEvent.InventoryReserved) => OrderState.Processing,
            (OrderState.Processing, OrderEvent.Shipped) => OrderState.Shipped,
            (OrderState.Shipped, OrderEvent.Delivered) => OrderState.Completed,
            _ => null
        };
    }
}
```

## Parallel Workflow Pattern

```csharp
public class ParallelWorkflowHandler : EverTaskHandler<ParallelWorkflowTask>
{
    public override async Task Handle(ParallelWorkflowTask task, CancellationToken ct)
    {
        // Dispatch multiple tasks in parallel
        var taskIds = new List<Guid>();

        taskIds.Add(await _dispatcher.Dispatch(new ProcessDataATask(task.WorkflowId)));
        taskIds.Add(await _dispatcher.Dispatch(new ProcessDataBTask(task.WorkflowId)));
        taskIds.Add(await _dispatcher.Dispatch(new ProcessDataCTask(task.WorkflowId)));

        // Store task IDs for coordination
        await StoreParallelTasksAsync(task.WorkflowId, taskIds);
    }
}

// Coordination handler checks if all parallel tasks complete
public class ParallelCoordinator : EverTaskHandler<ProcessDataATask>
{
    private Guid _workflowId;

    public override async Task Handle(ProcessDataATask task, CancellationToken ct)
    {
        _workflowId = task.WorkflowId;
        await ProcessAsync(task, ct);
    }

    public override async ValueTask OnCompleted(Guid taskId)
    {
        var allComplete = await CheckAllParallelTasksCompleteAsync(_workflowId);

        if (allComplete)
        {
            // All parallel tasks done - move to next stage
            await _dispatcher.Dispatch(new AggregateResultsTask(_workflowId));
        }
    }
}
```

## Delayed Execution Workflow

```csharp
public class DelayedWorkflowHandler : EverTaskHandler<DelayedWorkflowTask>
{
    private Guid _workflowId;
    private int _step;

    public override async Task Handle(DelayedWorkflowTask task, CancellationToken ct)
    {
        _workflowId = task.WorkflowId;
        _step = task.Step;

        await ProcessStepAsync(task.Step, ct);
    }

    public override async ValueTask OnCompleted(Guid taskId)
    {
        var nextStep = _step + 1;
        var delay = CalculateDelay(_step);

        // Schedule next step with delay
        await _dispatcher.Dispatch(
            new DelayedWorkflowTask(_workflowId, nextStep),
            delay);
    }

    private TimeSpan CalculateDelay(int step)
    {
        return step switch
        {
            1 => TimeSpan.FromMinutes(5),   // Short delay after step 1
            2 => TimeSpan.FromHours(1),     // Longer delay after step 2
            3 => TimeSpan.FromDays(1),      // Day delay after step 3
            _ => TimeSpan.Zero
        };
    }
}
```

## Best Practices

1. **Use Correlation IDs**: Track workflow instances across multiple tasks with unique identifiers
2. **Implement Compensation Logic**: Always handle failures with rollback/compensation tasks
3. **Keep State Minimal**: Pass only essential data between tasks; use IDs to load full state
4. **Make Tasks Idempotent**: Tasks should be safe to retry or run multiple times
5. **Log Workflow Progress**: Track state transitions and decisions for debugging
6. **Set Appropriate Timeouts**: Different workflow stages may need different timeout values
7. **Handle Partial Failures**: Design for graceful degradation when non-critical steps fail

## Next Steps

- [Task Orchestration](task-orchestration.md) - Continuations and cancellation patterns
- [Resilience](resilience.md) - Retry policies and error handling
- [Monitoring](monitoring.md) - Track workflow execution
