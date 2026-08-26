using System.Collections.Concurrent;

namespace EverTask.Scheduler.Occurrences;

/// <summary>
/// What <c>AddOccurrenceProvider&lt;T&gt;(key)</c> registered, and the one place a provider is resolved and
/// called (V2).
/// </summary>
/// <remarks>
/// A schedule row names a KEY, so this is the indirection that turns it back into an implementation. The
/// resolution happens in a FRESH SCOPE per call, which is what lets a provider depend on scoped services — a
/// DbContext holding the holiday table is the ordinary case — without any of them outliving the question they
/// were built to answer.
/// <para>
/// The scope is disposed ASYNCHRONOUSLY, like every other scope this library builds around user code. A
/// scoped dependency that implements only <see cref="IAsyncDisposable"/> — the shape a DbContext or a
/// repository often has — makes the synchronous disposal throw, and that throw happens after the provider
/// has answered perfectly well: it would be classified as a transient provider failure, so the schedule
/// would re-park and ask again for ever while every log line blamed a provider that never failed.
/// </para>
/// </remarks>
internal sealed class OccurrenceProviderRegistry(
    EverTaskServiceConfiguration options,
    IServiceScopeFactory scopeFactory)
{
    /// <summary>
    /// Cached per key: an implementation's determinism is a property of the implementation, so asking it once
    /// keeps <see cref="CatchUpOverflowPolicy.SkipOldest"/>'s dispatch-time gate from building a scope and a
    /// provider on every dispatch.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _deterministic = new(StringComparer.Ordinal);

    /// <summary>True when something is registered under <paramref name="key"/>.</summary>
    public bool IsRegistered(string key) => options.OccurrenceProviders.ContainsKey(key);

    /// <summary>
    /// <see cref="INextOccurrenceProvider.IsDeterministic"/> of the provider registered under
    /// <paramref name="key"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Nothing is registered under that key.</exception>
    public async ValueTask<bool> IsDeterministicAsync(string key)
    {
        if (_deterministic.TryGetValue(key, out var known))
            return known;

        var type = TypeOf(key);

        await using var scope = scopeFactory.CreateAsyncScope();

        var deterministic = Resolve(scope.ServiceProvider, type, key).IsDeterministic;

        _deterministic[key] = deterministic;

        return deterministic;
    }

    /// <summary>
    /// Asks the provider registered under <see cref="NextOccurrenceRequest.ProviderKey"/> for the occurrence
    /// after <see cref="NextOccurrenceRequest.AfterUtc"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Nothing is registered under that key.</exception>
    /// <remarks>
    /// Anything the provider itself does — throwing, or answering out of contract — is classified by the
    /// caller, which is where the schedule's identity and its retry accounting live.
    /// </remarks>
    public async ValueTask<DateTimeOffset?> GetNextOccurrenceAsync(NextOccurrenceRequest request,
                                                                   CancellationToken ct)
    {
        var type = TypeOf(request.ProviderKey);

        await using var scope = scopeFactory.CreateAsyncScope();

        var provider = Resolve(scope.ServiceProvider, type, request.ProviderKey);

        return await provider.GetNextOccurrenceAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Says that nothing is registered under <paramref name="key"/>, naming what IS.
    /// </summary>
    /// <remarks>
    /// A configuration error, never a transient one: a key nothing answers to does not start answering later,
    /// so it must not be retried the way a provider that threw is. It reaches a dispatch as an
    /// <see cref="ArgumentException"/> and a persisted row as the poison route every corrupt schedule takes.
    /// </remarks>
    public ArgumentException UnknownKey(string key)
    {
        var registered = options.OccurrenceProviders.Count == 0
                             ? "none is registered"
                             : $"the registered keys are {string.Join(", ", options.OccurrenceProviders.Keys)}";

        return new ArgumentException(
            $"No occurrence provider is registered under the key '{key}': {registered}. Register it with " +
            $"AddOccurrenceProvider<T>(\"{key}\") on the EverTask builder.", nameof(key));
    }

    /// <summary>The implementation type behind <paramref name="key"/>.</summary>
    /// <exception cref="ArgumentException">Nothing is registered under that key.</exception>
    private Type TypeOf(string key) =>
        options.OccurrenceProviders.TryGetValue(key, out var type) ? type : throw UnknownKey(key);

    private static INextOccurrenceProvider Resolve(IServiceProvider provider, Type type, string key) =>
        provider.GetService(type) as INextOccurrenceProvider
        ?? throw new InvalidOperationException(
            $"The occurrence provider registered under '{key}' ({type.Name}) could not be resolved from the " +
            "container.");
}
