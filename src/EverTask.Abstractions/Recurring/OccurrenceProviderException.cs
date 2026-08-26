namespace EverTask.Abstractions;

/// <summary>
/// Signals that an <see cref="INextOccurrenceProvider"/> could not answer: it threw, or it answered with an
/// instant at or before the one it was asked about.
/// </summary>
/// <remarks>
/// <para>
/// It is classified as TRANSIENT everywhere inside the library. A schedule whose provider fails writes
/// nothing — the cursor stays exactly where it was — and is parked again after a backoff, so the series
/// survives its calendar source being briefly unavailable. A crash in the meantime costs only the backoff:
/// the row is untouched, so startup recovery asks again.
/// </para>
/// <para>
/// It surfaces to a CALLER only on a dispatch (or a runtime reschedule), where there is no row to park yet
/// and the caller is the one holding the call. An unregistered provider key is a different thing altogether —
/// a configuration error, not a transient one — and raises an <see cref="ArgumentException"/> at dispatch,
/// while a persisted row that names one is poisoned by recovery like any other corrupt schedule.
/// </para>
/// </remarks>
public sealed class OccurrenceProviderException : Exception
{
    /// <summary>Creates a new provider failure.</summary>
    /// <param name="providerKey">The key of the provider that could not answer.</param>
    /// <param name="scheduleId">The schedule being computed, or <see cref="Guid.Empty"/> if it has no row yet.</param>
    /// <param name="message">What the provider did.</param>
    /// <param name="innerException">The exception it threw, if it threw one.</param>
    public OccurrenceProviderException(string providerKey, Guid scheduleId, string message,
                                       Exception? innerException = null) : base(message, innerException)
    {
        ProviderKey = providerKey;
        ScheduleId  = scheduleId;
    }

    /// <summary>The key of the provider that could not answer.</summary>
    public string ProviderKey { get; }

    /// <summary>The schedule whose grid was being computed, or <see cref="Guid.Empty"/> before its row exists.</summary>
    public Guid ScheduleId { get; }

    /// <summary>
    /// How long the schedule waits before its grid is asked again, decided by the consecutive failures this
    /// host has seen for it.
    /// </summary>
    internal TimeSpan RetryAfter { get; init; }

    /// <summary>How many times in a row this schedule's provider has failed on this host.</summary>
    internal int ConsecutiveFailures { get; init; }
}
