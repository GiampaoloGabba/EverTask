namespace EverTask.Scheduler.Occurrences;

internal static class BoundedOccurrenceWalker
{
    internal static async ValueTask<BoundedOccurrenceWalk> WalkAsync(
        DateTimeOffset startUtc, DateTimeOffset throughUtc, int limit, bool includeStart,
        Func<DateTimeOffset, ValueTask<DateTimeOffset?>> nextAsync,
        ICollection<DateTimeOffset>? visited = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        var count  = 0;
        var newest = startUtc;

        if (includeStart)
        {
            visited?.Add(startUtc);
            count = 1;
        }

        while (count < limit)
        {
            var following = await nextAsync(newest).ConfigureAwait(false);

            if (following is not { } next || next <= newest || next > throughUtc)
                return new BoundedOccurrenceWalk(count, newest, false);

            newest = next;
            visited?.Add(next);
            count++;
        }

        return new BoundedOccurrenceWalk(count, newest, true);
    }
}

internal readonly record struct BoundedOccurrenceWalk(int Count, DateTimeOffset NewestUtc, bool Bounded);
