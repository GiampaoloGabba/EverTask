global using System.Reflection;
global using System.Threading.Channels;
global using EverTask;
global using EverTask.Abstractions;
global using EverTask.Dispatcher;
global using EverTask.Handler;
global using EverTask.Logger;
global using EverTask.Monitoring;
global using EverTask.Resilience;
global using EverTask.Scheduler;
global using EverTask.Scheduler.Recurring;
global using EverTask.Scheduler.Recurring.Intervals;
global using EverTask.Serialization;
global using EverTask.Storage;
global using EverTask.Worker;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.DependencyInjection.Extensions;
global using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("EverTask.Tests")]
// B4/P2-4: lets the storage integration tests assert the REAL recovery read path (EverTaskJson.Deserialize)
// against rows written by the legacy producer — proving typed payload/schedule recovery, not just DB byte
// fidelity. Same trust already extended to EverTask.Tests; the serializer stays internal to consumers.
[assembly: InternalsVisibleTo("EverTask.Tests.Storage")]
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
