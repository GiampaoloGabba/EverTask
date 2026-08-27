namespace EverTask.Scheduler.Occurrences;

/// <summary>
/// Why a plan stopped where it did — which is also what decides where the schedule row is re-parked.
/// </summary>
internal enum DueSlotStopReason
{
    /// <summary>Everything the grid owed is in the plan (possibly nothing). Re-park at the new cursor.</summary>
    Exhausted = 0,

    /// <summary>
    /// More slots are due than the concurrency budget allows right now. The kick from the next occurrence that
    /// ends is the fast path back; the operational re-park is what guarantees progress if that kick is lost.
    /// </summary>
    WindowFull = 1,

    /// <summary>
    /// The backlog exceeded the per-episode cap and the overflow policy is
    /// <see cref="CatchUpOverflowPolicy.Halt"/>: nothing is materialized until an operator resumes the
    /// schedule.
    /// </summary>
    Halted = 2
}

/// <summary>
/// Which rule dropped a run of slots that will never become occurrences.
/// </summary>
/// <remarks>
/// The three are different decisions with different fixes — an age window too narrow for the outage, a
/// per-episode cap under <see cref="CatchUpOverflowPolicy.SkipOldest"/>, and the skip policy doing what it
/// says — so a dropped run is never reported under another's cause.
/// </remarks>
internal enum SlotLossReason
{
    /// <summary>Older than <c>now - MaxAge</c>: the age window of a replaying policy.</summary>
    MisfireWindowExceeded = 0,

    /// <summary>
    /// Older than the most recent <c>MaxOccurrences</c> slots, which is what
    /// <see cref="CatchUpOverflowPolicy.SkipOldest"/> keeps when the backlog exceeds the cap.
    /// </summary>
    CatchUpOverflow = 1,

    /// <summary>
    /// The skip policy itself: a slot that is no longer the current one is never replayed, whatever its age.
    /// </summary>
    SkippedByPolicy = 2
}

/// <summary>
/// One run of consecutive grid slots a plan drops, together with the rule that dropped it.
/// </summary>
/// <param name="Reason">Which rule dropped them.</param>
/// <param name="FromUtc">The oldest slot of the run.</param>
/// <param name="Count">How many slots went.</param>
/// <param name="IsExact">
/// Whether <paramref name="Count"/> is the real total or only a lower bound: a grid that has to be walked is
/// counted under a cap.
/// </param>
internal readonly record struct SlotLoss(SlotLossReason Reason, DateTimeOffset FromUtc, int Count, bool IsExact);

/// <summary>
/// What one materialization run of a durable schedule should do: which slots become occurrences, where the
/// cursor ends up, and what was dropped on the way.
/// </summary>
/// <remarks>
/// The slots are CONSECUTIVE grid points, so the cursor that follows slot <c>i</c> is slot <c>i + 1</c>, and
/// the one that follows the last is <see cref="NextCursorUtc"/>: each occurrence can then be written under its
/// own compare-and-swap on the cursor it advances, rather than one write nobody could resume. Skipping
/// therefore costs no write of its own while at least one slot survives; an empty plan whose
/// <see cref="NextCursorUtc"/> differs from the current cursor is the case where nothing survived.
/// </remarks>
internal sealed record DueSlotPlan
{
    /// <summary>The slots to materialize, oldest first. Consecutive on the grid.</summary>
    public IReadOnlyList<DateTimeOffset> Slots { get; init; } = [];

    /// <summary>
    /// The cursor to leave once every slot in <see cref="Slots"/> exists. <c>null</c> ends the series — the
    /// grid has nothing left inside the schedule's bounds, or the run budget has nothing left to spend.
    /// </summary>
    public DateTimeOffset? NextCursorUtc { get; init; }

    /// <summary>
    /// Whether the null cursor above comes from the RUN BUDGET rather than from the grid: this plan grants the
    /// last occurrence <c>MaxRuns</c> allows, so creating it ends the series.
    /// </summary>
    /// <remarks>
    /// The two reasons diverge once a run walks past a slot that already had a row: an ended grid is a fact
    /// about the SLOT the plan named, so the walk asks the grid again about the slot it reaches, while a spent
    /// budget is a fact about the WRITE and ends the series in the same commit that creates the last row.
    /// </remarks>
    public bool RunBudgetEndsSeries { get; init; }

    public DueSlotStopReason StopReason { get; init; }

    /// <summary>
    /// The runs of due slots this plan drops without ever running them, each with the rule that dropped it.
    /// </summary>
    /// <remarks>
    /// A list rather than one count: a single catch-up plan can lose slots twice for different reasons — the
    /// age window first, then the per-episode cap under <see cref="CatchUpOverflowPolicy.SkipOldest"/> — and
    /// the two must not be summed under one cause.
    /// </remarks>
    public IReadOnlyList<SlotLoss> Losses { get; init; } = [];

    /// <summary>
    /// A LOWER BOUND on the backlog that triggered a halt: counting stops one past the cap, so the number is
    /// "at least this many" and <see cref="IsExact"/> says so.
    /// </summary>
    public int DetectedAtLeast { get; init; }

    /// <summary>Whether <see cref="DetectedAtLeast"/> is the real total or only a lower bound.</summary>
    public bool IsExact { get; init; }

    /// <summary>
    /// The misfire metadata stamped on every occurrence this plan creates, or null when the schedule is simply
    /// keeping up. It describes the BACKLOG this run decided against, not the single slot: the same range and
    /// count for every row of one run, and the collapsed range for the single row of a fire-once.
    /// </summary>
    /// <remarks>
    /// The range and the count are two halves of one statement and must always agree: the count is how many
    /// grid slots fall inside the range, and across successive runs of the same catch-up both shrink together.
    /// </remarks>
    public OccurrenceMisfire? Misfire { get; init; }
}

/// <summary>
/// The persisted half of <see cref="MisfireInfo"/>: what the DECISION knew, which by the time the occurrence
/// runs no longer exists to be measured. Lateness is not here — that one belongs to the delivery.
/// </summary>
/// <param name="Kind">Which policy produced this occurrence.</param>
/// <param name="MissedFromUtc">The oldest slot of the backlog the decision saw.</param>
/// <param name="MissedThroughUtc">The newest slot of that backlog.</param>
/// <param name="MissedCount">How many grid slots the range holds, its two ends included.</param>
/// <param name="MissedCountIsExact">
/// Whether <paramref name="MissedCount"/> is the real total or only a lower bound: a grid that has to be
/// walked is counted under a cap.
/// </param>
internal readonly record struct OccurrenceMisfire(
    MisfireKind Kind,
    DateTimeOffset MissedFromUtc,
    DateTimeOffset MissedThroughUtc,
    int MissedCount,
    bool MissedCountIsExact);
