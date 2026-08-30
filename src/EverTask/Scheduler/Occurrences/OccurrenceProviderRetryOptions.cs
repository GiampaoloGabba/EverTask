namespace EverTask.Scheduler.Occurrences;

/// <summary>
/// How long a schedule waits before retrying an occurrence evaluation after a provider could not answer or an
/// exclusion search exhausted its budget.
/// </summary>
/// <remarks>
/// Neither failure is the end of a series, so the schedule keeps its cursor and comes back. The wait doubles from
/// <see cref="InitialBackoff"/> at each consecutive failure of the same schedule and stops at
/// <see cref="MaxBackoff"/>; one answer resets it.
/// </remarks>
public sealed class OccurrenceProviderRetryOptions
{
    /// <summary>
    /// The longest either bound may be. Both are added to a UTC instant on every re-park, so a value in
    /// centuries overflows that addition and leaves the schedule parked nowhere at all.
    /// </summary>
    internal static readonly TimeSpan MaxRetryBackoff = TimeSpan.FromDays(1);

    private TimeSpan _initialBackoff = TimeSpan.FromMinutes(1);
    private TimeSpan _maxBackoff     = TimeSpan.FromMinutes(15);

    /// <summary>How long the first failure waits. Default: one minute.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not positive, or longer than a day.</exception>
    public TimeSpan InitialBackoff
    {
        get => _initialBackoff;
        set
        {
            Validate(value, nameof(InitialBackoff));
            _initialBackoff = value;
        }
    }

    /// <summary>
    /// The longest wait the doubling reaches. Default: fifteen minutes. Values below
    /// <see cref="InitialBackoff"/> simply make every wait that long.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Not positive, or longer than a day.</exception>
    public TimeSpan MaxBackoff
    {
        get => _maxBackoff;
        set
        {
            Validate(value, nameof(MaxBackoff));
            _maxBackoff = value;
        }
    }

    private static void Validate(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name, value,
                "The occurrence-evaluation backoff must be positive: a schedule that retries with no wait at " +
                "all hammers the source that just failed.");
        }

        if (value > MaxRetryBackoff)
        {
            throw new ArgumentOutOfRangeException(name, value,
                "The occurrence-evaluation backoff must be at most one day: it is what brings a stalled " +
                "schedule back without a restart, and a longer one is indistinguishable from none.");
        }
    }
}
