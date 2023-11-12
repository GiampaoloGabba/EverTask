namespace Microsoft.Extensions.DependencyInjection;

public class EverTaskServiceBuilder
{
    public IServiceCollection Services { get; private set; }

    internal EverTaskServiceBuilder(IServiceCollection services)
    {
        Services = services;
    }
}
