namespace EverTask.Abstractions;

/// <summary>
/// The three limits a catch-up replay is bounded by, plus how it behaves when it hits the second one.
/// </summary>
/// <remarks>
/// The two caps are mandatory and have no defaults on purpose: they answer two different questions — how far
/// back a replay may reach, and how much work one episode may create — and neither has an answer that is right
/// for every schedule. A per-second grid left behind by a three-month downtime owes eight million slots; the
/// age window alone would not stop it.
/// </remarks>
public sealed class CatchUpOptions
{
    private readonly int _maxPendingOccurrences = 1;
    private readonly CatchUpOverflowPolicy _overflowPolicy = CatchUpOverflowPolicy.Halt;

    /// <param name="maxAge">How far back the replay may reach. See <see cref="MaxAge"/>.</param>
    /// <param name="maxOccurrences">How many slots one episode may replay. See <see cref="MaxOccurrences"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">Either cap is zero or negative.</exception>
    public CatchUpOptions(TimeSpan maxAge, int maxOccurrences)
    {
        if (maxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAge), maxAge,
                "The catch-up age window must be positive: a zero window would drop every missed slot.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxOccurrences, 1);

        MaxAge         = maxAge;
        MaxOccurrences = maxOccurrences;
    }

    /// <summary>
    /// Slots older than <c>now - MaxAge</c> are never replayed. This is the ONE ordinary way a durable
    /// schedule loses a slot, and it is always reported.
    /// </summary>
    public TimeSpan MaxAge { get; }

    /// <summary>
    /// The most slots one catch-up episode may replay in total. Exceeding it is handled by
    /// <see cref="OverflowPolicy"/>.
    /// </summary>
    public int MaxOccurrences { get; }

    /// <summary>
    /// What to do when the backlog exceeds <see cref="MaxOccurrences"/>. Default:
    /// <see cref="CatchUpOverflowPolicy.Halt"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Not one of the defined values.</exception>
    public CatchUpOverflowPolicy OverflowPolicy
    {
        get => _overflowPolicy;
        init
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "Not a defined overflow policy.");

            _overflowPolicy = value;
        }
    }

    /// <summary>
    /// How many occurrences of this schedule may be alive at the same time. <c>1</c> (the default) makes the
    /// replay strictly serial: the next slot is only created once the previous occurrence has ended, which is
    /// also what keeps a handler that overruns its own period from overlapping itself.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Less than one.</exception>
    public int MaxPendingOccurrences
    {
        get => _maxPendingOccurrences;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _maxPendingOccurrences = value;
        }
    }
}
