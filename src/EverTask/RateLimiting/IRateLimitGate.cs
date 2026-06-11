namespace EverTask.RateLimiting;

/// <summary>
/// Outcome of a rate-limit gate pass.
/// </summary>
public enum RateLimitGateOutcome
{
    /// <summary>Budget acquired (or gate not applicable): the caller executes the task now.</summary>
    Proceed = 0,

    /// <summary>
    /// The task was handled by the gate (re-parked into the scheduler at the reserved slot, or
    /// its occurrence dropped): the caller MUST NOT execute it and MUST NOT run any
    /// post-execution logic.
    /// </summary>
    Deferred = 1
}

/// <summary>
/// Result of a rate-limit gate pass.
/// </summary>
/// <param name="Outcome">Whether the caller may execute the task.</param>
/// <param name="SlotUtc">The reserved slot (UTC) when <paramref name="Outcome"/> is Deferred.</param>
/// <param name="EmitDeferralEvent">
/// True when the caller should publish a deferral monitoring event for this pass. Aggregated at
/// the source: the gate signals the first deferral per (task type, key) per window plus periodic
/// summaries, so sustained throttling does not turn into an event storm.
/// </param>
/// <param name="AggregatedDeferrals">
/// Number of deferrals represented by this event (1 for the first of a window, more for a
/// periodic summary). Meaningful only when <paramref name="EmitDeferralEvent"/> is true.
/// </param>
public readonly record struct RateLimitGateResult(
    RateLimitGateOutcome Outcome,
    DateTimeOffset SlotUtc = default,
    bool EmitDeferralEvent = false,
    int AggregatedDeferrals = 0);

/// <summary>
/// Consumer-side rate-limit gate: decides — without ever blocking a worker on budget — whether a
/// dequeued task may execute now, awaits near slots inline, and re-parks deferred tasks into the
/// in-memory scheduler at their reserved slot. Extracted from the worker for testability.
/// </summary>
/// <remarks>
/// Invariants the gate guarantees to its caller:
/// <list type="bullet">
/// <item><description>A deferral writes NOTHING to storage: the task status stays <c>Queued</c>
/// (already covered by startup recovery), and the only storage touch of a deferral cycle is the
/// existing <c>SetQueued</c> when the slot fires and the task re-enters a worker queue.</description></item>
/// <item><description>The Deferred outcome means the task must NOT execute: the caller must
/// return without entering its execution path (post-execution logic like recurring
/// re-scheduling would corrupt run counts).</description></item>
/// <item><description>If the limiter itself fails, the gate fails OPEN (the task executes, with
/// a warning log) — consistent with the never-lose-a-task contract.</description></item>
/// </list>
/// </remarks>
public interface IRateLimitGate
{
    /// <summary>
    /// Attempts to pass the gate for a dequeued task carrying a rate-limit policy and key.
    /// </summary>
    /// <param name="task">The dequeued task (with <c>RateLimitPolicy</c> and <c>RateLimitKey</c> stamped).</param>
    /// <param name="ct">The service cancellation token.</param>
    /// <returns>The gate decision; see <see cref="RateLimitGateResult"/>.</returns>
    ValueTask<RateLimitGateResult> TryPassAsync(TaskHandlerExecutor task, CancellationToken ct);
}
