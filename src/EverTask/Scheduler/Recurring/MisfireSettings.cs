using System.Text.Json.Serialization;

namespace EverTask.Scheduler.Recurring;

/// <summary>
/// The persisted form of a schedule's misfire policy: one flat shape covering all three policies, because the
/// definition it lives on is a serialized row and a polymorphic member would need a declared alias set to
/// round-trip at all (see the payload contract).
/// </summary>
/// <remarks>
/// The typed options the fluent API takes (<see cref="CatchUpOptions"/>, <see cref="FireOnceOptions"/>) are
/// what a caller sees; this is what a row carries. Which fields are meaningful follows from
/// <see cref="Policy"/>: <see cref="MisfirePolicy.Skip"/> uses none, <see cref="MisfirePolicy.FireOnce"/> only
/// <see cref="MaxAge"/>, <see cref="MisfirePolicy.CatchUp"/> all of them.
/// </remarks>
public class MisfireSettings
{
    /// <summary>Public and parameterless for the serializer, like every interval type (payload contract).</summary>
    [JsonConstructor]
    public MisfireSettings() { }

    /// <summary>What happens to a slot that came due while nothing was there to run it.</summary>
    public MisfirePolicy Policy { get; set; }

    /// <summary>How far back a replay may reach. Required by <see cref="MisfirePolicy.CatchUp"/>.</summary>
    public TimeSpan? MaxAge { get; set; }

    /// <summary>
    /// How many slots one catch-up episode may replay. Required by <see cref="MisfirePolicy.CatchUp"/>.
    /// </summary>
    public int? MaxOccurrences { get; set; }

    /// <summary>What to do when the backlog exceeds <see cref="MaxOccurrences"/>.</summary>
    public CatchUpOverflowPolicy OverflowPolicy { get; set; }

    /// <summary>How many occurrences of this schedule may be alive at once. One means strictly serial.</summary>
    public int MaxPendingOccurrences { get; set; } = 1;

    /// <summary>
    /// Validates the shape against its own policy, on every path that accepts a schedule — the fluent build,
    /// a definition handed to the dispatcher directly, and a recovery deserialize.
    /// </summary>
    /// <remarks>
    /// A definition that reaches recovery with a corrupt policy is treated exactly like an unparseable cron:
    /// the row is poisoned terminally rather than silently degraded to a policy nobody asked for. That is why
    /// the enum values are checked too — the tolerant converter passes an unknown numeric value through.
    /// </remarks>
    internal void Validate()
    {
        if (!Enum.IsDefined(Policy))
            throw new ArgumentException($"Invalid MisfirePolicy '{(int)Policy}': not a defined value.", nameof(Policy));

        if (!CatchUpConstraints.IsValidOverflowPolicy(OverflowPolicy))
        {
            throw new ArgumentException(
                $"Invalid CatchUpOverflowPolicy '{(int)OverflowPolicy}': not a defined value.",
                nameof(OverflowPolicy));
        }

        if (!CatchUpConstraints.IsValidPendingCap(MaxPendingOccurrences))
        {
            throw new ArgumentException(
                $"MaxPendingOccurrences must be at least 1, was {MaxPendingOccurrences}: a schedule that may " +
                "have no occurrence alive would never make progress.", nameof(MaxPendingOccurrences));
        }

        if (MaxAge is { } age && !CatchUpConstraints.IsValidAge(age))
            throw new ArgumentException($"The misfire age window must be positive, was {age}.", nameof(MaxAge));

        if (Policy != MisfirePolicy.CatchUp)
            return;

        if (MaxAge == null || MaxOccurrences == null)
        {
            throw new ArgumentException(
                "A catch-up policy needs both MaxAge and MaxOccurrences: without them a long downtime would " +
                "replay an unbounded backlog.", nameof(Policy));
        }

        if (!CatchUpConstraints.IsValidEpisodeCap(MaxOccurrences.Value))
        {
            throw new ArgumentException(
                $"MaxOccurrences must be at least 1, was {MaxOccurrences}.", nameof(MaxOccurrences));
        }
    }

    /// <summary>The human-readable tail appended to a schedule's description.</summary>
    internal string Describe() => Policy switch
    {
        MisfirePolicy.FireOnce => MaxAge is { } age
                                      ? $"on misfire fire once (within {age})"
                                      : "on misfire fire once",
        MisfirePolicy.CatchUp => $"on misfire catch up (within {MaxAge}, up to {MaxOccurrences} per episode, " +
                                 $"{OverflowPolicy} on overflow, {MaxPendingOccurrences} pending at a time)",
        _ => "on misfire skip"
    };
}
