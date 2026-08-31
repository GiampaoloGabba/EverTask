namespace EverTask.Abstractions;

/// <summary>
/// One question put to an <see cref="INextOccurrenceProvider"/>: which occurrence follows
/// <see cref="AfterUtc"/>, and for which schedule.
/// </summary>
/// <remarks>
/// A record with init-only members rather than positional parameters: a later member can then be added
/// without changing the constructor an already-compiled provider binds to.
/// </remarks>
public sealed record NextOccurrenceRequest
{
    /// <summary>The key the schedule was registered under (<c>AddOccurrenceProvider&lt;T&gt;(key)</c>).</summary>
    public required string ProviderKey { get; init; }

    /// <summary>
    /// The opaque configuration string the schedule carries, or <c>null</c>. EverTask never reads it: what it
    /// means, and how its format is versioned, belongs to the provider.
    /// </summary>
    public string? Config { get; init; }

    /// <summary>
    /// The instant to answer after. The occurrence returned must be STRICTLY later than this.
    /// </summary>
    public required DateTimeOffset AfterUtc { get; init; }

    /// <summary>
    /// The IANA id of the schedule's time zone, or <c>null</c> when it names none. EverTask does no
    /// conversion for a provider — the answer is taken as UTC — so a calendar that means "09:00 in Rome" has
    /// to read this and resolve it itself.
    /// </summary>
    public string? TimeZoneId { get; init; }

    /// <summary>
    /// The 1-based number of the run being computed, when it is known. Zero while the schedule has no run to
    /// number yet (a plan that probes the grid rather than producing the next occurrence).
    /// </summary>
    public int RunNumber { get; init; }

    /// <summary>The dispatch key of the schedule, when it has one.</summary>
    public string? TaskKey { get; init; }

    /// <summary>
    /// The id of the schedule row, or <see cref="Guid.Empty"/> while it does not exist yet — the very first
    /// occurrence of a brand-new dispatch is computed before the row is written. <see cref="TaskKey"/> is the
    /// identity that is stable across both.
    /// </summary>
    public Guid ScheduleId { get; init; }
}
