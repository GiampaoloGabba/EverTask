using EverTask.Logger;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Xunit;

namespace EverTask.Tests.Logging.Serilog;

public class ServiceRegistrationTests
{
    [Fact]
    public void Should_be_registered_and_resolved_correctly()
    {
        var services = new ServiceCollection();

        services.AddEverTask(opt=>opt.RegisterTasksFromAssembly(typeof(ServiceRegistrationTests).Assembly))
                .AddSerilog();

        var serviceProvider = services.BuildServiceProvider();

        var service = serviceProvider.GetService<IEverTaskLogger<ServiceRegistrationTests>>();
        var serilog = serviceProvider.GetService<ILogger>();

        Assert.NotNull(service);
        Assert.NotNull(serilog);
        Assert.IsType<EverTaskLogger<ServiceRegistrationTests>>(service);
    }
}

