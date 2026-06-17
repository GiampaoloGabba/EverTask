# Template — task records + handlers

Tasks are `record … : IEverTask` with **public properties only, IDs not entities**
(validate against `references/08-payload-contract.md`). Handlers use **primary constructors** for DI.

## Simple task + handler

```csharp
public record SendWelcomeEmailTask(string UserEmail, string UserName) : IEverTask;

public class SendWelcomeEmailHandler(IEmailService emailService)
    : EverTaskHandler<SendWelcomeEmailTask>
{
    public override async Task Handle(SendWelcomeEmailTask task, CancellationToken ct)
        => await emailService.SendAsync(task.UserEmail, task.UserName, ct);
}
```

Dispatch: `await dispatcher.Dispatch(new SendWelcomeEmailTask(user.Email, user.Name));`

## Handler with lifecycle callbacks, retry & timeout

```csharp
public record ProcessPaymentTask(Guid PaymentId) : IEverTask;

public class ProcessPaymentHandler(IPaymentGateway gateway, ITaskDispatcher dispatcher)
    : EverTaskHandler<ProcessPaymentTask>
{
    public override TimeSpan? Timeout => TimeSpan.FromMinutes(2);

    public override IRetryPolicy? RetryPolicy =>
        new LinearRetryPolicy(5, TimeSpan.FromSeconds(2)).HandleTransientNetworkErrors();

    public override async Task Handle(ProcessPaymentTask task, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await gateway.ChargeAsync(task.PaymentId, ct);
    }

    public override ValueTask OnStarted(Guid id)
    { Logger.LogInformation("Payment {Id} started", id); return ValueTask.CompletedTask; }

    public override ValueTask OnRetry(Guid id, int attempt, Exception ex, TimeSpan delay)
    { Logger.LogWarning(ex, "Payment {Id} retry #{N} after {Ms}ms", id, attempt, delay.TotalMilliseconds); return ValueTask.CompletedTask; }

    public override async ValueTask OnCompleted(Guid id)
        => await dispatcher.Dispatch(new SendReceiptTask(id));

    public override async ValueTask OnError(Guid id, Exception? ex, string? msg)
    {
        Logger.LogError(ex, "Payment {Id} failed: {Msg}", id, msg);
        await dispatcher.Dispatch(new RefundTask(id));    // compensation
    }
}
```

## Recurring task (handler is identical; the schedule is set at dispatch)

```csharp
public record DailyCleanupTask() : IEverTask;

public class DailyCleanupHandler(AppDbContext db) : EverTaskHandler<DailyCleanupTask>
{
    public override async Task Handle(DailyCleanupTask task, CancellationToken ct)
        => await db.StaleRows().ExecuteDeleteAsync(ct);   // own DI scope → scoped DbContext is safe
}
```

Dispatch (see `RecurringRegistrar.md` for idempotent startup registration):
`await dispatcher.Dispatch(new DailyCleanupTask(), r => r.Schedule().EveryDay().AtTime(new TimeOnly(3,0)), taskKey: "daily-cleanup");`

## Rate-limited task (per tenant)

```csharp
public record SyncTenantData(Guid TenantId) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => TenantId.ToString();
}

public class SyncTenantDataHandler(IExternalApi api) : EverTaskHandler<SyncTenantData>
{
    public override RateLimitPolicy? RateLimitPolicy =>
        new RateLimitPolicy(permits: 15, period: TimeSpan.FromMinutes(1));  // 15 calls/min per tenant

    public override string? QueueName => "integrations";   // optional: isolate on a named queue

    public override async Task Handle(SyncTenantData task, CancellationToken ct)
        => await api.SyncAsync(task.TenantId, ct);
}
```

## Polymorphic payload (when a property must hold a base/interface type — avoids ET0004)

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(EmailChannel), "email")]
[JsonDerivedType(typeof(SmsChannel),   "sms")]
public abstract record NotificationChannel;
public record EmailChannel(string Address) : NotificationChannel;
public record SmsChannel(string Number)     : NotificationChannel;

public record NotifyTask(Guid UserId, NotificationChannel Channel) : IEverTask;
```

> Prefer flattening (an enum discriminator + nullable fields) over polymorphism when feasible.
