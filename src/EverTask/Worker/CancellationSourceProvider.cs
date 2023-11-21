using System.Collections.Concurrent;

namespace EverTask.Worker;

public interface ICancellationSourceProvider
{
    CancellationToken CreateToken(Guid id, CancellationToken sourceToken);
    CancellationTokenSource? TryGet(Guid id);
    void Delete(Guid id);
    void CancelTokenForTask(Guid id);
}

internal sealed class CancellationSourceProvider : ICancellationSourceProvider
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _sources = new();

    public CancellationToken CreateToken(Guid id, CancellationToken sourceToken)
    {
        if (_sources.ContainsKey(id))
            Delete(id);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(sourceToken);
        _sources.TryAdd(id, cts);

        return cts.Token;
    }

    public CancellationTokenSource? TryGet(Guid id)
    {
        _sources.TryGetValue(id, out var cts);
        return cts;
    }

    public void Delete(Guid id)
    {
        if (_sources.TryRemove(id, out var cts))
        {
            cts.Dispose();
        }
    }

    public void CancelTokenForTask(Guid id)
    {
        if (_sources.TryGetValue(id, out var cts))
        {
            cts.Cancel();
            Delete(id);
        }
    }

}
