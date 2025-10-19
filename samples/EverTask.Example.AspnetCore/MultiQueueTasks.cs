using EverTask.Abstractions;
using EverTask.Resilience;

namespace EverTask.Example.AspnetCore;

// High-priority task that processes payments
public record ProcessPaymentTask(Guid PaymentId, decimal Amount, string Currency) : IEverTask;

public class ProcessPaymentHandler : EverTaskHandler<ProcessPaymentTask>
{
    private readonly ILogger<ProcessPaymentHandler> _logger;

    // Route this handler to the high-priority queue
    public override string? QueueName => "high-priority";

    public ProcessPaymentHandler(ILogger<ProcessPaymentHandler> logger)
    {
        _logger = logger;

        // Configure retry policy for critical payment processing
        RetryPolicy = new LinearRetryPolicy(5, TimeSpan.FromSeconds(2));
        Timeout = TimeSpan.FromMinutes(5);
    }

    public override async Task Handle(ProcessPaymentTask task, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing payment {PaymentId} for {Amount} {Currency} in HIGH-PRIORITY queue",
            task.PaymentId, task.Amount, task.Currency);

        // Simulate payment processing
        await Task.Delay(Random.Shared.Next(500, 2000), cancellationToken);

        _logger.LogInformation("Payment {PaymentId} processed successfully", task.PaymentId);
    }

    public override ValueTask OnError(Guid persistenceId, Exception? exception, string? message)
    {
        _logger.LogError(exception, "Payment processing failed for task {PersistenceId}: {Message}",
            persistenceId, message);
        return ValueTask.CompletedTask;
    }
}

// Background task that generates reports
public record GenerateReportTask(string ReportType, DateOnly StartDate, DateOnly EndDate) : IEverTask;

public class GenerateReportHandler : EverTaskHandler<GenerateReportTask>
{
    private readonly ILogger<GenerateReportHandler> _logger;

    // Route this handler to the background queue
    public override string? QueueName => "background";

    // Mark as CPU-bound since report generation is intensive
    public GenerateReportHandler(ILogger<GenerateReportHandler> logger)
    {
        _logger = logger;
        CpuBoundOperation = true;
        Timeout = TimeSpan.FromMinutes(30);
    }

    public override async Task Handle(GenerateReportTask task, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Generating {ReportType} report from {StartDate} to {EndDate} in BACKGROUND queue",
            task.ReportType, task.StartDate, task.EndDate);

        // Simulate intensive report generation
        await Task.Delay(Random.Shared.Next(3000, 10000), cancellationToken);

        _logger.LogInformation("{ReportType} report completed", task.ReportType);
    }
}

// Email notification task that uses the default queue
public record SendEmailTask(string To, string Subject, string Body) : IEverTask;

public class SendEmailHandler : EverTaskHandler<SendEmailTask>
{
    private readonly ILogger<SendEmailHandler> _logger;

    // No QueueName specified - will use default queue
    public SendEmailHandler(ILogger<SendEmailHandler> logger)
    {
        _logger = logger;
        RetryPolicy = new LinearRetryPolicy(3, TimeSpan.FromSeconds(5));
        Timeout = TimeSpan.FromMinutes(2);
    }

    public override async Task Handle(SendEmailTask task, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Sending email to {To} with subject '{Subject}' in DEFAULT queue",
            task.To, task.Subject);

        // Simulate email sending
        await Task.Delay(Random.Shared.Next(200, 1000), cancellationToken);

        _logger.LogInformation("Email sent to {To}", task.To);
    }
}

// Recurring cleanup task that will automatically use the "recurring" queue
public record CleanupExpiredDataTask : IEverTask;

public class CleanupExpiredDataHandler : EverTaskHandler<CleanupExpiredDataTask>
{
    private readonly ILogger<CleanupExpiredDataHandler> _logger;

    // No QueueName specified - will automatically route to "recurring" queue when scheduled as recurring
    public CleanupExpiredDataHandler(ILogger<CleanupExpiredDataHandler> logger)
    {
        _logger = logger;
    }

    public override async Task Handle(CleanupExpiredDataTask task, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting expired data cleanup in RECURRING queue");

        // Simulate cleanup operation
        await Task.Delay(Random.Shared.Next(1000, 3000), cancellationToken);

        var recordsDeleted = Random.Shared.Next(10, 100);
        _logger.LogInformation("Cleanup completed, deleted {RecordsDeleted} expired records", recordsDeleted);
    }
}

// Image processing task with explicit queue selection
public record ProcessImageTask(string ImagePath, string Operation) : IEverTask;

public class ProcessImageHandler : EverTaskHandler<ProcessImageTask>
{
    private readonly ILogger<ProcessImageHandler> _logger;

    // Route to background queue for CPU-intensive image processing
    public override string? QueueName => "background";

    public ProcessImageHandler(ILogger<ProcessImageHandler> logger)
    {
        _logger = logger;
        CpuBoundOperation = true;
        Timeout = TimeSpan.FromMinutes(10);
    }

    public override async Task Handle(ProcessImageTask task, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing image {ImagePath} with operation {Operation} in BACKGROUND queue",
            task.ImagePath, task.Operation);

        // Simulate intensive image processing
        await Task.Delay(Random.Shared.Next(2000, 5000), cancellationToken);

        _logger.LogInformation("Image {ImagePath} processed successfully", task.ImagePath);
    }
}