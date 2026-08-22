using System.Reflection;
using Microsoft.Extensions.Logging;

namespace EverTask.Tests.Logging;

/// <summary>
/// Shared assertion over the source-generated <c>[LoggerMessage]</c> methods of the EverTask assemblies (#32).
/// The generator does not diagnose duplicate EventIds, so the solution-wide uniqueness of the explicit ids
/// (and the presence of an explicit, non-zero id on every method) is enforced here; each test project runs it
/// over the assemblies it references.
/// </summary>
public static class LoggerMessageEventIdAssert
{
    public static void AssertExplicitAndUnique(params Assembly[] assemblies)
    {
        var methods = assemblies
            .Distinct()
            .SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Select(m => (Method: m, Attribute: m.GetCustomAttribute<LoggerMessageAttribute>()))
            .Where(x => x.Attribute != null)
            .ToList();

        methods.ShouldNotBeEmpty("no [LoggerMessage] method found: wrong assemblies, or the generator is not in use");

        var missing = methods
            .Where(x => x.Attribute!.EventId <= 0)
            .Select(x => $"{x.Method.DeclaringType!.FullName}.{x.Method.Name}")
            .ToList();
        missing.ShouldBeEmpty($"[LoggerMessage] methods without an explicit positive EventId: {string.Join(", ", missing)}");

        var duplicates = methods
            .GroupBy(x => x.Attribute!.EventId)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => $"{x.Method.DeclaringType!.Name}.{x.Method.Name}"))}")
            .ToList();
        duplicates.ShouldBeEmpty($"duplicate EventIds: {string.Join(" | ", duplicates)}");
    }
}
