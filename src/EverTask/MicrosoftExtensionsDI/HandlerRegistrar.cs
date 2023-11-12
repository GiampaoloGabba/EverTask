namespace Microsoft.Extensions.DependencyInjection;

internal static class HandlerRegistrar
{
    public static void RegisterConnectedImplementations(IServiceCollection services,
                                                        IEnumerable<Assembly> assembliesToScan)
    {
        var requestInterface = typeof(IEverTaskHandler<>);
        var concretions      = new List<Type>();
        var interfaces       = new List<Type>();

        foreach (var type in assembliesToScan.SelectMany(a => a.DefinedTypes).Where(t => !t.IsOpenGeneric()))
        {
            var interfaceTypes = type.FindInterfacesThatClose(requestInterface).ToArray();
            if (!interfaceTypes.Any()) continue;

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

            foreach (var type in exactMatches)
            {
                services.TryAddTransient(@interface, type);
            }

            if (!@interface.IsOpenGeneric())
            {
                AddConcretionsThatCouldBeClosed(@interface, concretions, services);
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

    private static void AddConcretionsThatCouldBeClosed(Type @interface, List<Type> concretions,
                                                        IServiceCollection services)
    {
        foreach (var type in concretions.Where(x => x.IsOpenGeneric() && x.CouldCloseTo(@interface)))
        {
            try
            {
                services.TryAddTransient(@interface, type.MakeGenericType(@interface.GenericTypeArguments));
            }
            catch (Exception)
            {
            }
        }
    }

    private static bool CouldCloseTo(this Type openConcretion, Type closedInterface)
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
