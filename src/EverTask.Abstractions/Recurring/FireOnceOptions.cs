namespace EverTask.Abstractions;

/// <summary>
/// The single knob of <see cref="MisfirePolicy.FireOnce"/>: how stale the collapsed run of missed slots may be
/// before it is dropped instead of fired.
/// </summary>
public sealed class FireOnceOptions
{
    private readonly TimeSpan? _maxAge;

    /// <summary>
    /// Drop the catch-up entirely when even the most recent missed slot is older than <c>now - MaxAge</c>.
    /// <c>null</c> (the default) fires whatever the age of the missed run.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Zero or negative.</exception>
    public TimeSpan? MaxAge
    {
        get => _maxAge;
        init
        {
            if (value is { } age && age <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    "The fire-once age window must be positive, or null for no window at all.");
            }

            _maxAge = value;
        }
    }
}
