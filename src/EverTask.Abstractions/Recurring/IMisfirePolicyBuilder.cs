namespace EverTask.Abstractions;

/// <summary>
/// Picks the schedule's misfire policy. One call, typed per policy: each policy carries exactly the options it
/// has, instead of one bag of settings where most fields are ignored by whichever policy is chosen.
/// </summary>
public interface IMisfirePolicyBuilder
{
    /// <summary>
    /// Drop missed slots (the default): at most the slot that is still current runs, then the schedule moves
    /// on to its next future occurrence.
    /// </summary>
    void Skip();

    /// <summary>
    /// Collapse a run of missed slots into ONE occurrence, at the most recent of them. The schedule becomes
    /// <see cref="OccurrenceMode.Durable"/>.
    /// </summary>
    /// <param name="options">Optional age window; <c>null</c> fires however stale the run is.</param>
    void FireOnce(FireOnceOptions? options = null);

    /// <summary>
    /// Replay every missed slot as its own occurrence, oldest first, inside <paramref name="options"/>. The
    /// schedule becomes <see cref="OccurrenceMode.Durable"/>.
    /// </summary>
    /// <param name="options">The age window, the per-episode cap and the concurrency budget. Required.</param>
    void CatchUp(CatchUpOptions options);
}
