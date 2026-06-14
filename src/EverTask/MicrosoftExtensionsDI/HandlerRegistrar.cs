namespace Microsoft.Extensions.DependencyInjection;

// This code was adapted from MediatR by Jimmy Bogard.
// Specific inspiration was taken from the ServiceRegistrar.cs file.
// Source: https://github.com/jbogard/MediatR/blob/master/src/MediatR/Registration/ServiceRegistrar.cs

internal static class HandlerRegistrar
{
    public static void RegisterConnectedImplementations(IServiceCollection services,
                                                        IEnumerable<Assembly> assembliesToScan,
                                                        ICollection<string>? warnings = null)
    {
        var requestInterface = typeof(IEverTaskHandler<>);
        var concretions      = new List<Type>();
        var interfaces       = new List<Type>();

        foreach (var type in assembliesToScan.SelectMany(a => a.DefinedTypes))
        {
            var interfaceTypes = type.FindInterfacesThatClose(requestInterface).ToArray();
            if (!interfaceTypes.Any()) continue;

            if (type.IsOpenGeneric())
            {
                // G1: open-generic handlers are not supported — the closing path below was dead code
                // (open generics were filtered out before reaching it), so they were silently dropped.
                // Surface them instead of letting the task type resolve to no handler at runtime.
                warnings?.Add(
                    $"Open-generic handler '{type.FullName}' implementing IEverTaskHandler<> is not supported " +
                    "and was ignored. Register a closed handler for each concrete task type.");
                continue;
            }

            if (type.IsConcrete())
            {
                concretions.Add(type);
            }

            foreach (var interfaceType in interfaceTypes)
            {
                interfaces.Fill(interfaceType);
            }
        }

        foreach (var @interface in interfaces)
        {
            var exactMatches = concretions.Where(x => x.CanBeCastTo(@interface)).ToList();
            if (exactMatches.Count > 1)
            {
                exactMatches.RemoveAll(m => !IsMatchingWithInterface(m, @interface));
            }

            if (exactMatches.Count > 1)
            {
                // G2: multiple concrete handlers for the same closed interface. Registration is
                // first-wins (TryAdd), but the ambiguity must not be silent.
                var chosen   = exactMatches[0];
                var ignored  = string.Join(", ", exactMatches.Skip(1).Select(t => t.Name));
                var taskName = @interface.GenericTypeArguments.FirstOrDefault()?.Name ?? @interface.Name;
                warnings?.Add(
                    $"Multiple handlers found for task '{taskName}': using '{chosen.Name}', ignoring [{ignored}]. " +
                    "Register only one handler per task type.");
            }

            foreach (var type in exactMatches)
            {
                // Register handler by interface (IEverTaskHandler<TTask> → ConcreteHandler)
                // This is used by eager mode handler resolution in TaskHandlerWrapper
                services.TryAddTransient(@interface, type);

                // Register handler by concrete type (ConcreteHandler → ConcreteHandler)
                // This is used by lazy mode handler resolution in TaskHandlerExecutor.GetOrResolveHandler()
                // when resolving handlers from their AssemblyQualifiedName stored in HandlerTypeName.
                // Memory overhead: ~36 bytes per handler registration (negligible).
                services.TryAddTransient(type, type);
            }
        }
    }

    private static bool IsMatchingWithInterface(Type? handlerType, Type? handlerInterface)
    {
        if (handlerType == null || handlerInterface == null)
        {
            return false;
        }

        if (handlerType.IsInterface)
        {
            if (handlerType.GenericTypeArguments.SequenceEqual(handlerInterface.GenericTypeArguments))
            {
                return true;
            }
        }
        else
        {
            return IsMatchingWithInterface(handlerType.GetInterface(handlerInterface.Name), handlerInterface);
        }

        return false;
    }

    internal static bool CouldCloseTo(this Type openConcretion, Type closedInterface)
    {
        var openInterface = closedInterface.GetGenericTypeDefinition();
        var arguments     = closedInterface.GenericTypeArguments;

        var concreteArguments = openConcretion.GenericTypeArguments;
        return arguments.Length == concreteArguments.Length && openConcretion.CanBeCastTo(openInterface);
    }

    private static bool CanBeCastTo(this Type? pluggedType, Type pluginType)
    {
        if (pluggedType == null) return false;
        return pluggedType == pluginType || pluginType.IsAssignableFrom(pluggedType);
    }

    private static bool IsOpenGeneric(this Type type) =>
        type.IsGenericTypeDefinition || type.ContainsGenericParameters;

    private static IEnumerable<Type> FindInterfacesThatClose(this Type? pluggedType, Type templateType) =>
        FindInterfacesThatClosesCore(pluggedType, templateType).Distinct();

    private static IEnumerable<Type> FindInterfacesThatClosesCore(Type? pluggedType, Type templateType)
    {
        if (pluggedType == null) yield break;

        if (!pluggedType.IsConcrete()) yield break;

        if (templateType.IsInterface)
        {
            foreach (
                var interfaceType in
                pluggedType.GetInterfaces()
                           .Where(type => type.IsGenericType && (type.GetGenericTypeDefinition() == templateType)))
            {
                yield return interfaceType;
            }
        }
        else if (pluggedType.BaseType!.IsGenericType &&
                 (pluggedType.BaseType!.GetGenericTypeDefinition() == templateType))
        {
            yield return pluggedType.BaseType!;
        }

        if (pluggedType.BaseType == typeof(object)) yield break;

        foreach (var interfaceType in FindInterfacesThatClosesCore(pluggedType.BaseType!, templateType))
        {
            yield return interfaceType;
        }
    }

    private static bool IsConcrete(this Type type) =>
        type is { IsAbstract: false, IsInterface: false };

    private static void Fill<T>(this ICollection<T> list, T value)
    {
        if (list.Contains(value)) return;
        list.Add(value);
    }
}
