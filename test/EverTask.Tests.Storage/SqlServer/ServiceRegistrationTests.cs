using EverTask.EfCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace EverTask.Tests.Storage.SqlServer;

public class ServiceRegistrationTests
{
    [Fact]
    public void Should_be_registered_and_resolved_correctly()
    {
        var services = new ServiceCollection();

        services.AddEverTask(opt=>opt.RegisterTasksFromAssembly(typeof(ServiceRegistrationTests).Assembly))
                .AddSqlServerStorage("Server=(localdb)\\mssqllocaldb;Database=CliClubTestDb;Trusted_Connection=True;MultipleActiveResultSets=true", opt => opt.AutoApplyMigrations = true);

        var serviceProvider = services.BuildServiceProvider();

        var taskStore = serviceProvider.GetService<ITaskStoreDbContext>();
        var dbContext = serviceProvider.GetService<ITaskStoreDbContext>();
        var options = serviceProvider.GetService<IOptions<TaskStoreOptions>>();

        Assert.NotNull(taskStore);
        Assert.NotNull(dbContext);
        Assert.NotNull(options);

    }
}

