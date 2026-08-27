global using System.Reflection;
global using System.Threading.Channels;
global using EverTask;
global using EverTask.Abstractions;
global using EverTask.Dispatcher;
global using EverTask.Handler;
global using EverTask.Logger;
global using EverTask.Monitoring;
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
// Lets the storage integration tests assert the REAL recovery read path (EverTaskJson.Deserialize) against
// rows written by the legacy producer, not just DB byte fidelity.
[assembly: InternalsVisibleTo("EverTask.Tests.Storage")]
// The monitoring API reads a row's schedule facts (the serialized RecurringTask, both shapes of RuntimeInfo)
// with EverTaskJson's lenient options: a second reader in the API project would drift from them. Read-only
// trust — nothing internal is written from there.
[assembly: InternalsVisibleTo("EverTask.Monitor.Api")]
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
