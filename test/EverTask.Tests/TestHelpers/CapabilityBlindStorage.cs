using EverTask.Logger;
using EverTask.Storage;

namespace EverTask.Tests.TestHelpers;

/// <summary>
/// The in-memory storage with either capability flag turned off: the shape of a custom store that never
/// implemented the atomic operations behind one of them.
/// </summary>
/// <remarks>
/// It has to be a real storage, not a mock: the host resolves it for everything else it does, and a double
/// that answered nothing would fail long before the refusal under test. Re-listing <see cref="ITaskStorage"/>
/// is what lets the two hidden properties take the interface slots — the base declares them non-virtual,
/// because a storage does not change its mind about what it implements.
/// </remarks>
internal sealed class CapabilityBlindStorage(
    IEverTaskLogger<MemoryTaskStorage> logger,
    bool scheduleVersioning = false,
    bool durableOccurrences = false) : MemoryTaskStorage(logger), ITaskStorage
{
    public new bool SupportsScheduleVersioning => scheduleVersioning;

    public new bool SupportsDurableOccurrences => durableOccurrences;
}
