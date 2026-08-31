namespace EverTask.Abstractions;

internal static class CatchUpConstraints
{
    internal static bool IsValidAge(TimeSpan value) => value > TimeSpan.Zero;

    internal static bool IsValidEpisodeCap(int value) => value >= 1;

    internal static bool IsValidPendingCap(int value) => value >= 1;

    internal static bool IsValidOverflowPolicy(CatchUpOverflowPolicy value) => Enum.IsDefined(value);
}
