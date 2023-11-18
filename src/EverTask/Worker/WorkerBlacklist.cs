using System.Collections.Concurrent;

namespace EverTask.Worker;

internal sealed class WorkerBlacklist : IWorkerBlacklist
{
    private readonly ConcurrentDictionary<Guid, EmptyStruct> _blacklist = new();

    public void Add(Guid guid)
    {
        _blacklist.TryAdd(guid, new EmptyStruct());
    }

    public bool IsBlacklisted(Guid guid)
    {
        return _blacklist.ContainsKey(guid);
    }

    public void Remove(Guid guid)
    {
        _blacklist.TryRemove(guid, out _);
    }

    struct EmptyStruct;
}
