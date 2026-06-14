using EverTask.Abstractions;
using EverTask.Logger;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;
using Microsoft.Extensions.Logging;

namespace EverTask.Tests;

/// <summary>
/// M8 batch A — the audit trail produced by MemoryTaskStorage must match the relational providers
/// for the same event (EfCore/SqlServer use the shared IsRealError / audit policy):
/// F19/L29 (ServiceStopped + OCE/null must not audit at Minimal/ErrorsOnly), L43 (recovery Queued
/// transition must audit at Full), L28 (RunsAudit.ExecutedAt stamped at the current time).
/// </summary>
public class MemoryStorageAuditConsistencyTests
{
    private readonly MemoryTaskStorage _storage = new(new Mock<IEverTaskLogger<MemoryTaskStorage>>().Object);

    private async Task<Guid> SeedAsync(
        QueuedTaskStatus status = QueuedTaskStatus.InProgress, DateTimeOffset? lastExecutionUtc = null)
    {
        var id = TestGuidGenerator.New();
        await _storage.Persist(new QueuedTask
        {
            Id               = id,
            Type             = "T",
            Request          = "{}",
            Handler          = "H",
            CreatedAtUtc     = DateTimeOffset.UtcNow,
            Status           = status,
            LastExecutionUtc = lastExecutionUtc
        });
        return id;
    }

    private async Task<QueuedTask> ReloadAsync(Guid id) =>
        (await _storage.Get(t => t.Id == id))[0];

    [Fact]
    public async Task Should_not_audit_servicestopped_with_cancellation_at_minimal_level()
    {
        // F19: ServiceStopped carrying an OperationCanceledException is expected shutdown, not an error.
        // The relational providers skip the audit at Minimal/ErrorsOnly; Memory must match.
        var id = await SeedAsync();

        await _storage.SetStatus(id, QueuedTaskStatus.ServiceStopped,
            new OperationCanceledException(), AuditLevel.Minimal);

        var task = await ReloadAsync(id);
        task.StatusAudits.Count(s => s.NewStatus == QueuedTaskStatus.ServiceStopped).ShouldBe(0);
    }

    [Fact]
    public async Task Should_not_audit_servicestopped_with_null_exception_at_minimal_level()
    {
        // L29: a ServiceStopped with a null exception is not a real error either — relational providers
        // do not audit it at Minimal/ErrorsOnly, Memory must match.
        var id = await SeedAsync();

        await _storage.SetStatus(id, QueuedTaskStatus.ServiceStopped, null, AuditLevel.ErrorsOnly);

        var task = await ReloadAsync(id);
        task.StatusAudits.Count(s => s.NewStatus == QueuedTaskStatus.ServiceStopped).ShouldBe(0);
    }

    [Fact]
    public async Task Should_audit_real_failure_at_minimal_level()
    {
        // Regression guard: a genuine Failed status is still audited at Minimal on Memory.
        var id = await SeedAsync();

        await _storage.SetStatus(id, QueuedTaskStatus.Failed,
            new InvalidOperationException("boom"), AuditLevel.Minimal);

        var task = await ReloadAsync(id);
        task.StatusAudits.Count(s => s.NewStatus == QueuedTaskStatus.Failed).ShouldBe(1);
    }

    [Fact]
    public async Task Should_audit_queued_recovery_transition_at_full_level()
    {
        // L43: the recovery Queued transition must be audited at Full, like the relational providers
        // (and like Memory's own live SetQueued). Pre-fix Memory wrote no audit at any level.
        var id = await SeedAsync(QueuedTaskStatus.WaitingQueue);

        (await _storage.TrySetQueuedIfRecoverable(id, AuditLevel.Full)).ShouldBeTrue();

        var task = await ReloadAsync(id);
        task.Status.ShouldBe(QueuedTaskStatus.Queued);
        task.StatusAudits.Count(s => s.NewStatus == QueuedTaskStatus.Queued).ShouldBe(1);
    }

    [Fact]
    public async Task Should_not_audit_queued_recovery_transition_at_none_level()
    {
        // Guard: None never audits.
        var id = await SeedAsync(QueuedTaskStatus.WaitingQueue);

        (await _storage.TrySetQueuedIfRecoverable(id, AuditLevel.None)).ShouldBeTrue();

        var task = await ReloadAsync(id);
        task.Status.ShouldBe(QueuedTaskStatus.Queued);
        task.StatusAudits.Count(s => s.NewStatus == QueuedTaskStatus.Queued).ShouldBe(0);
    }

    [Fact]
    public async Task Should_stamp_runsaudit_executedat_at_current_time()
    {
        // L28: RunsAudit.ExecutedAt is the moment of the run-count update (as the relational providers
        // stamp it), not the task's older LastExecutionUtc.
        var staleLastExecution = DateTimeOffset.UtcNow.AddHours(-1);
        var id = await SeedAsync(QueuedTaskStatus.Completed, staleLastExecution);

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await _storage.UpdateCurrentRun(id, 12.5, DateTimeOffset.UtcNow.AddMinutes(5), AuditLevel.Full);

        var task = await ReloadAsync(id);
        var runsAudit = task.RunsAudits.ShouldHaveSingleItem();
        runsAudit.ExecutedAt.ShouldBeGreaterThan(before);
        runsAudit.ExecutedAt.ShouldBeGreaterThan(staleLastExecution.AddMinutes(30));
    }
}
