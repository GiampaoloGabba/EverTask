global using System.Net;
global using EverTask.Abstractions;
global using EverTask.Worker;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Hosting;
global using Moq;
global using Shouldly;
global using Xunit;

// The storage suite reuses this assembly's test helpers, and the recovery harness hands back the internal
// WorkerService — an internal helper is the only shape that can, since a public one exposing an internal
// return type does not compile.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("EverTask.Tests.Storage")]
