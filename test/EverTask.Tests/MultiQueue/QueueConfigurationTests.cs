using System.Threading.Channels;
using EverTask.Abstractions;
using EverTask.Configuration;
using EverTask.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EverTask.Tests.MultiQueue;

public class QueueConfigurationTests
{
    [Fact]
    public void EverTaskServiceBuilder_ConfigureDefaultQueue_UpdatesConfiguration()
    {
        // Arrange
        var services = new ServiceCollection();
        var config = new EverTaskServiceConfiguration();
        config.RegisterTasksFromAssembly(typeof(QueueConfigurationTests).Assembly);
        var builder = new EverTaskServiceBuilder(services, config);

        // Act
        builder.ConfigureDefaultQueue(q => q
            .SetMaxDegreeOfParallelism(10)
            .SetChannelCapacity(1000)
            .SetFullBehavior(QueueFullBehavior.FallbackToDefault));

        // Assert
        Assert.True(config.Queues.ContainsKey("default"));
        var defaultQueue = config.Queues["default"];
        Assert.Equal(10, defaultQueue.MaxDegreeOfParallelism);
        Assert.Equal(1000, defaultQueue.ChannelOptions.Capacity);
        Assert.Equal(QueueFullBehavior.FallbackToDefault, defaultQueue.QueueFullBehavior);
    }

    [Fact]
    public void EverTaskServiceBuilder_AddQueue_CreatesNewQueue()
    {
        // Arrange
        var services = new ServiceCollection();
        var config = new EverTaskServiceConfiguration();
        config.RegisterTasksFromAssembly(typeof(QueueConfigurationTests).Assembly);
        var builder = new EverTaskServiceBuilder(services, config);

        // Act
        builder.AddQueue("high-priority", q => q
            .SetMaxDegreeOfParallelism(20)
            .SetChannelCapacity(2000)
            .SetFullBehavior(QueueFullBehavior.Wait));

        // Assert
        Assert.True(config.Queues.ContainsKey("high-priority"));
        var queue = config.Queues["high-priority"];
        Assert.Equal("high-priority", queue.Name);
        Assert.Equal(20, queue.MaxDegreeOfParallelism);
        Assert.Equal(2000, queue.ChannelOptions.Capacity);
        Assert.Equal(QueueFullBehavior.Wait, queue.QueueFullBehavior);
    }

    [Fact]
    public void EverTaskServiceBuilder_ConfigureRecurringQueue_CreatesRecurringQueue()
    {
        // Arrange
        var services = new ServiceCollection();
        var config = new EverTaskServiceConfiguration();
        config.RegisterTasksFromAssembly(typeof(QueueConfigurationTests).Assembly);
        var builder = new EverTaskServiceBuilder(services, config);

        // Act
        builder.ConfigureRecurringQueue(q => q
            .SetMaxDegreeOfParallelism(5)
            .SetChannelCapacity(500));

        // Assert
        Assert.True(config.Queues.ContainsKey("recurring"));
        var queue = config.Queues["recurring"];
        Assert.Equal("recurring", queue.Name);
        Assert.Equal(5, queue.MaxDegreeOfParallelism);
        Assert.Equal(500, queue.ChannelOptions.Capacity);
    }

    [Fact]
    public void EverTaskServiceBuilder_MultipleQueues_CanBeConfigured()
    {
        // Arrange
        var services = new ServiceCollection();
        var config = new EverTaskServiceConfiguration();
        config.RegisterTasksFromAssembly(typeof(QueueConfigurationTests).Assembly);
        var builder = new EverTaskServiceBuilder(services, config);

        // Act
        builder
            .ConfigureDefaultQueue(q => q.SetMaxDegreeOfParallelism(10))
            .AddQueue("critical", q => q.SetMaxDegreeOfParallelism(20))
            .AddQueue("background", q => q.SetMaxDegreeOfParallelism(2))
            .ConfigureRecurringQueue(q => q.SetMaxDegreeOfParallelism(5));

        // Assert
        Assert.Equal(4, config.Queues.Count);
        Assert.Equal(10, config.Queues["default"].MaxDegreeOfParallelism);
        Assert.Equal(20, config.Queues["critical"].MaxDegreeOfParallelism);
        Assert.Equal(2, config.Queues["background"].MaxDegreeOfParallelism);
        Assert.Equal(5, config.Queues["recurring"].MaxDegreeOfParallelism);
    }

    [Fact]
    public void QueueConfiguration_FluentMethods_ReturnSelf()
    {
        // Arrange
        var config = new QueueConfiguration();

        // Act & Assert - Chain all fluent methods
        var result = config
            .SetMaxDegreeOfParallelism(10)
            .SetChannelCapacity(1000)
            .SetChannelOptions(new BoundedChannelOptions(500))
            .SetDefaultRetryPolicy(new LinearRetryPolicy(3, TimeSpan.FromSeconds(1)))
            .SetDefaultTimeout(TimeSpan.FromMinutes(5))
            .SetFullBehavior(QueueFullBehavior.ThrowException);

        Assert.Same(config, result);
        Assert.Equal(10, config.MaxDegreeOfParallelism);
        Assert.Equal(500, config.ChannelOptions.Capacity); // SetChannelOptions overwrites
        Assert.Equal(QueueFullBehavior.ThrowException, config.QueueFullBehavior);
    }

    [Fact]
    public void EverTaskServiceBuilder_AddQueue_ThrowsForEmptyName()
    {
        // Arrange
        var services = new ServiceCollection();
        var config = new EverTaskServiceConfiguration();
        config.RegisterTasksFromAssembly(typeof(QueueConfigurationTests).Assembly);
        var builder = new EverTaskServiceBuilder(services, config);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => builder.AddQueue(""));
        Assert.Throws<ArgumentException>(() => builder.AddQueue("   "));
        Assert.Throws<ArgumentException>(() => builder.AddQueue(null!));
    }

    [Fact]
    public void QueueConfiguration_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var config = new QueueConfiguration();

        // Assert
        Assert.Equal("default", config.Name);
        Assert.Equal(1, config.MaxDegreeOfParallelism);
        Assert.Equal(500, config.ChannelOptions.Capacity);
        Assert.Equal(BoundedChannelFullMode.Wait, config.ChannelOptions.FullMode);
        Assert.Equal(QueueFullBehavior.FallbackToDefault, config.QueueFullBehavior);
        Assert.Null(config.DefaultRetryPolicy);
        Assert.Null(config.DefaultTimeout);
    }

    [Fact]
    public void EverTaskServiceBuilder_EnsureRecurringQueue_CreatesIfNotExists()
    {
        // Arrange
        var services = new ServiceCollection();
        var config = new EverTaskServiceConfiguration();
        config.RegisterTasksFromAssembly(typeof(QueueConfigurationTests).Assembly);
        // Set up default queue
        config.Queues["default"] = new QueueConfiguration
        {
            Name = "default",
            MaxDegreeOfParallelism = 10
        };
        var builder = new EverTaskServiceBuilder(services, config);

        // Act
        builder.EnsureRecurringQueue();

        // Assert
        Assert.True(config.Queues.ContainsKey("recurring"));
        var recurringQueue = config.Queues["recurring"];
        Assert.Equal("recurring", recurringQueue.Name);
        Assert.Equal(10, recurringQueue.MaxDegreeOfParallelism); // Cloned from default
    }

    [Fact]
    public void EverTaskServiceBuilder_EnsureRecurringQueue_DoesNotOverwriteExisting()
    {
        // Arrange
        var services = new ServiceCollection();
        var config = new EverTaskServiceConfiguration();
        config.RegisterTasksFromAssembly(typeof(QueueConfigurationTests).Assembly);
        config.Queues["recurring"] = new QueueConfiguration
        {
            Name = "recurring",
            MaxDegreeOfParallelism = 15
        };
        var builder = new EverTaskServiceBuilder(services, config);

        // Act
        builder.EnsureRecurringQueue();

        // Assert
        Assert.Equal(15, config.Queues["recurring"].MaxDegreeOfParallelism); // Not overwritten
    }
}