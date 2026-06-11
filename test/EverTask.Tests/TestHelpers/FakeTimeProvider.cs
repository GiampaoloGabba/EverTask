namespace EverTask.Tests.TestHelpers;

/// <summary>
/// Deterministic manual clock for unit tests: time moves only via <see cref="Advance"/>.
/// </summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public FakeTimeProvider(DateTimeOffset? startUtc = null)
    {
        _utcNow = startUtc ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow += delta;
}
