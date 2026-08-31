// EverTask's DI wiring lives in Microsoft.Extensions.DependencyInjection so it surfaces without
// extra usings, per the .NET hosting-extensions convention (same as ServiceCollectionExtensions).
// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Startup-time assembly scanner: discovers concrete <see cref="IEverTaskHandler{TTask}"/>
/// implementations and registers them in the service collection. One-shot code — no caches,
/// one reflection pass per assembly; unsupported open-generic handlers and duplicate closed
/// handlers are collected as warnings and logged by <c>WorkerService</c> when the host starts.
/// </summary>
internal static class HandlerRegistrar
{
    public static void RegisterConnectedImplementations(IServiceCollection services,
                                                        IEnumerable<Assembly> assembliesToScan,
                                                        ICollection<string>? warnings = null)
    {
        var contract = typeof(IEverTaskHandler<>);

        // Buckets keyed by the EXACT closed interface type. Never match by assignability here:
        // IEverTaskHandler<in TTask> is contravariant, so IsAssignableFrom would admit a
        // base-task handler as a candidate for every derived task's contract. The dictionary
        // gives O(1) exact lookup; the list preserves discovery order so first-wins stays
        // deterministic (dictionary enumeration order is unspecified).
        var buckets        = new Dictionary<Type, HandlerBucket>();
        var bucketsInOrder = new List<HandlerBucket>();

        foreach (var assembly in assembliesToScan)
        {
            // DefinedTypes includes internal and nested types. A ReflectionTypeLoadException from
            // a partially loadable assembly propagates deliberately: the caller explicitly asked
            // for this assembly, and starting without its handlers would only move the failure to
            // dispatch time — or to recovery, after tasks were already persisted.
            foreach (var candidate in assembly.DefinedTypes)
            {
                if (candidate.IsAbstract || candidate.IsInterface)
                    continue;

                // GetInterfaces() flattens the whole inheritance chain, so handlers deriving from
                // EverTaskHandler<T> (or deeper hierarchies) surface without walking base types.
                var implemented = candidate.GetInterfaces();
                var isOpen      = candidate.IsGenericTypeDefinition || candidate.ContainsGenericParameters;
                var isHandler   = false;

                foreach (var service in implemented)
                {
                    if (!service.IsGenericType || service.GetGenericTypeDefinition() != contract)
                        continue;

                    isHandler = true;

                    // G1: an open-generic handler can never be activated for a concrete task type;
                    // it is skipped entirely and surfaced once below, after the interface loop.
                    if (isOpen)
                        continue;

                    if (buckets.TryGetValue(service, out var bucket))
                    {
                        bucket.AddDuplicate(candidate);
                    }
                    else
                    {
                        bucket = new HandlerBucket(service, candidate);
                        buckets.Add(service, bucket);
                        bucketsInOrder.Add(bucket);
                    }
                }

                if (isHandler && isOpen)
                {
                    warnings?.Add(
                        $"Open-generic handler '{candidate.FullName ?? candidate.Name}' implementing IEverTaskHandler<> " +
                        "is not supported and was ignored. Register a closed handler for each concrete task type.");
                }
            }
        }

        foreach (var bucket in bucketsInOrder)
        {
            if (warnings != null && bucket.Duplicates is { Count: > 0 } duplicates)
            {
                // G2: first-wins is deterministic (assembly order, then DefinedTypes order) but the
                // ambiguity must never be silent. "Scanner selected" because a descriptor registered
                // before AddEverTask still takes precedence through TryAddTransient below. Handlers
                // are qualified with their assembly: the message cites assembly discovery order, and
                // same-named handlers from different assemblies are otherwise indistinguishable.
                var taskName = QualifiedTypeName(bucket.Contract.GenericTypeArguments[0]);
                warnings.Add(
                    $"Multiple handlers found for task '{taskName}': scanner selected '{Describe(bucket.Winner)}' and " +
                    $"ignored [{string.Join(", ", duplicates.Select(Describe))}] by assembly/DefinedTypes " +
                    "discovery order. Register only one handler per task type.");
            }

            // Interface binding: eager resolution (TaskHandlerWrapperImp) and the lazy interface
            // fallback resolve IEverTaskHandler<TTask> directly.
            services.TryAddTransient(bucket.Contract, bucket.Winner);

            // Concrete self-binding: lazy resolution re-resolves by the AssemblyQualifiedName stored
            // in the queue row, and the eager path re-resolves the concrete type so a manual shared
            // INTERFACE registration does not leak one instance across concurrent executions (G3).
            services.TryAddTransient(bucket.Winner, bucket.Winner);

            if (bucket.Duplicates == null)
                continue;

            // Losing duplicates stay resolvable by concrete type: a persisted row may carry the
            // AssemblyQualifiedName of a handler that only lost the scan after a redeploy
            // reordered assemblies or types.
            foreach (var duplicate in bucket.Duplicates)
                services.TryAddTransient(duplicate, duplicate);
        }
    }

    /// <summary>
    /// Handler identity for the G2 warning: namespace-qualified friendly name plus the assembly
    /// simple name — the diagnostic cites assembly discovery order, so it must show the
    /// assemblies, and two same-named handlers must stay distinguishable.
    /// </summary>
    private static string Describe(Type handler) =>
        $"{QualifiedTypeName(handler)} ({handler.Assembly.GetName().Name})";

    private static string QualifiedTypeName(Type type) =>
        type.Namespace is { Length: > 0 } ns ? $"{ns}.{FriendlyTypeName(type)}" : FriendlyTypeName(type);

    /// <summary>
    /// Readable type name for diagnostics: declaring-type chain preserved and generic arguments
    /// expanded at the level that declares them (<c>Outer&lt;Int32&gt;.InnerTask</c>,
    /// <c>ImportTask&lt;CsvRow&gt;</c> — never the raw FullName, unreadable for closed generics).
    /// Reflection surfaces enclosing generic parameters on nested types, hence the arity split.
    /// </summary>
    private static string FriendlyTypeName(Type type)
    {
        var allArgs = type.IsGenericType ? type.GetGenericArguments() : Type.EmptyTypes;
        return Render(type, allArgs, allArgs.Length);

        static string Render(Type current, Type[] allArgs, int argCount)
        {
            var declaring  = current.DeclaringType;
            var outerCount = Math.Min(declaring?.GetGenericArguments().Length ?? 0, argCount);

            var name = current.Name;
            var tick = name.IndexOf('`', StringComparison.Ordinal);
            if (tick > 0)
                name = name[..tick];

            if (argCount > outerCount)
            {
                var ownArgs = allArgs.Skip(outerCount).Take(argCount - outerCount).Select(FriendlyTypeName);
                name = $"{name}<{string.Join(", ", ownArgs)}>";
            }

            return declaring != null ? $"{Render(declaring, allArgs, outerCount)}.{name}" : name;
        }
    }

    /// <summary>
    /// The concrete handlers discovered for one exact closed <c>IEverTaskHandler&lt;TTask&gt;</c>
    /// interface: the first type encountered wins, later ones are kept for the G2 warning and for
    /// concrete self-registration. The duplicate list is allocated only when a duplicate exists.
    /// </summary>
    private sealed class HandlerBucket(Type contract, Type winner)
    {
        public Type        Contract   { get; } = contract;
        public Type        Winner     { get; } = winner;
        public List<Type>? Duplicates { get; private set; }

        public void AddDuplicate(Type handler)
        {
            // Guards the anomalous re-encounter of the same Type without a per-bucket HashSet
            // (duplicate lists are tiny).
            if (handler == Winner || Duplicates?.Contains(handler) == true)
                return;

            (Duplicates ??= []).Add(handler);
        }
    }
}
