using EverTask.Resilience;

namespace Microsoft.Extensions.DependencyInjection;

public class EverTaskServiceConfiguration
{
    internal BoundedChannelOptions ChannelOptions = new(500)
    {
        FullMode = BoundedChannelFullMode.Wait
    };

    internal int MaxDegreeOfParallelism = 1;

    internal bool ThrowIfUnableToPersist = true;
    internal List<Assembly> AssembliesToRegister { get; } = new();

    internal IRetryPolicy DefaultRetryPolicy { get; set; } = new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(500));

    internal TimeSpan? DefaultTimeout { get; set; } = null;

    public EverTaskServiceConfiguration SetChannelOptions(int capacity)
    {
        ChannelOptions.Capacity = capacity;
        return this;
    }

    public EverTaskServiceConfiguration SetChannelOptions(BoundedChannelOptions options)
    {
        ChannelOptions = options;
        return this;
    }

    public EverTaskServiceConfiguration SetMaxDegreeOfParallelism(int parallelism)
    {
        MaxDegreeOfParallelism = parallelism;
        return this;
    }

    public EverTaskServiceConfiguration SetThrowIfUnableToPersist(bool value)
    {
        ThrowIfUnableToPersist = value;
        return this;
    }

    public EverTaskServiceConfiguration SetDefaultRetryPolicy(IRetryPolicy policy)
    {
        DefaultRetryPolicy = policy;
        return this;
    }

    public EverTaskServiceConfiguration SetDefaultTimeout(TimeSpan? timeout)
    {
        DefaultTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Register various EverTask handlers from assembly
    /// </summary>
    /// <param name="assembly">Assembly to scan</param>
    /// <returns>This</returns>
    public EverTaskServiceConfiguration RegisterTasksFromAssembly(Assembly assembly)
    {
        AssembliesToRegister.Add(assembly);
        return this;
    }

    /// <summary>
    /// Register various EverTask handlers from assemblies
    /// </summary>
    /// <param name="assemblies">Assemblies to scan</param>
    /// <returns>This</returns>
    public EverTaskServiceConfiguration RegisterTasksFromAssemblies(
        params Assembly[] assemblies)
    {
        AssembliesToRegister.AddRange(assemblies);
        return this;
    }
}
