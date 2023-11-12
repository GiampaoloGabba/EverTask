global using System.Reflection;
global using System.Threading.Channels;
global using EverTask;
global using EverTask.Abstractions;
global using EverTask.Handler;
global using EverTask.Storage;
global using EverTask.Worker;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.DependencyInjection.Extensions;
global using Microsoft.Extensions.Logging;
global using Newtonsoft.Json;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("EverTask.Tests")]
