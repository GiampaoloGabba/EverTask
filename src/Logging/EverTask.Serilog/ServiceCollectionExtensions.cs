﻿using EverTask;
using EverTask.Logger;
using EverTask.Serilog;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;

namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static EverTaskServiceBuilder AddSerilog(this EverTaskServiceBuilder builder,
                                                    Action<LoggerConfiguration>? configure = null)
    {
        var loggerConfiguration = new LoggerConfiguration();
        if (configure == null)
        {
            loggerConfiguration.WriteTo.Console();
        }
        else
        {
            configure(loggerConfiguration);
        }

        var logger = loggerConfiguration.CreateLogger();
        builder.Services.TryAddSingleton<ILogger>(logger);
        builder.Services.TryAddSingleton(typeof(IEverTaskLogger<>), typeof(EverTaskSerilogLogger<>));

        return builder;
    }
}
