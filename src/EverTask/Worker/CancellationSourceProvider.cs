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
        // Create new CTS that will be used or disposed
        var newCts = CancellationTokenSource.CreateLinkedTokenSource(sourceToken);

        // Use AddOrUpdate to avoid race conditions between check and add
        var actualCts = _sources.AddOrUpdate(
            id,
            newCts, // Add factory - use new CTS if key doesn't exist
            (_, existingCts) => // Update factory - replace existing CTS
            {
                // Dispose the existing one and replace with new
                try { existingCts.Dispose(); } catch { /* Ignore disposal errors */ }
                return newCts;
            });

        // If AddOrUpdate didn't use our newCts (race condition), dispose it
        if (actualCts != newCts)
        {
            newCts.Dispose();
        }

        return actualCts.Token;
    }

    public CancellationTokenSource? TryGet(Guid id) => _sources.GetValueOrDefault(id);

    public void Delete(Guid id)
    {
        if (!_sources.TryRemove(id, out var cts)) return;

        try
        {
            cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed by another thread, ignore
        }
    }

    public void CancelTokenForTask(Guid id)
    {
        if (!_sources.TryGetValue(id, out var cts)) return;
        cts.Cancel();
        Delete(id);
    }

}
