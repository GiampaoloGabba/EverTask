using System.Collections.Concurrent;

namespace EverTask.Handler;

/// <summary>
/// Process-wide cache of assembly-qualified type names.
/// Shared by the dispatch path (<see cref="TaskHandlerWrapper"/>), the persistence path
/// (<see cref="TaskHandlerExecutorExtensions.ToQueuedTask"/>) and lazy conversions
/// (<see cref="TaskHandlerExecutor.ToLazy"/>) so the same string instance is reused
/// instead of being regenerated for every dispatch.
/// </summary>
internal static class TypeNameCache
{
    private static readonly ConcurrentDictionary<Type, string> Cache = new();

    /// <summary>
    /// Returns the cached <see cref="Type.AssemblyQualifiedName"/> for the given type.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the type has no assembly-qualified name.</exception>
    public static string GetAssemblyQualifiedName(Type type) =>
        Cache.GetOrAdd(type, static t => t.AssemblyQualifiedName
                                         ?? throw new InvalidOperationException(
                                             $"Type {t} has no AssemblyQualifiedName"));
}
