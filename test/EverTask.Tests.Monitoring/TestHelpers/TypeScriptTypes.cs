namespace EverTask.Tests.Monitoring.TestHelpers;

/// <summary>
/// The dashboard's <c>.types.ts</c> files, read as declarations rather than as text: interface name to its
/// keys and whether each is optional, with <c>extends</c> resolved.
/// </summary>
/// <remarks>
/// Shared by the two suites that hold the browser's view of a wire to the wire itself — the REST DTOs
/// (<c>API/Controllers/JsonContractTests</c>) and the SignalR event record
/// (<c>SignalR/EventWireContractTests</c>). One parser, because a second copy would be a copy that drifts on
/// the very files it exists to watch.
/// </remarks>
public sealed class TypeScriptTypes
{
    public enum KeyState { Missing, Required, Optional }

    private readonly Dictionary<string, (string? Base, Dictionary<string, bool> Keys)> _interfaces =
        new(StringComparer.Ordinal);

    public bool Declares(string name) => _interfaces.ContainsKey(name);

    public KeyState StateOf(string interfaceName, string key)
    {
        var name = interfaceName;

        while (name is not null && _interfaces.TryGetValue(name, out var declaration))
        {
            if (declaration.Keys.TryGetValue(key, out var optional))
                return optional ? KeyState.Optional : KeyState.Required;

            name = declaration.Base;
        }

        return KeyState.Missing;
    }

    public static TypeScriptTypes Load()
    {
        var typesDirectory = Path.Combine(RepositoryRoot(), "src", "Monitoring", "EverTask.Monitor.Api",
            "UI", "src", "types");

        var parsed = new TypeScriptTypes();

        foreach (var file in Directory.GetFiles(typesDirectory, "*.types.ts"))
            parsed.Parse(File.ReadAllLines(file));

        parsed._interfaces.ShouldNotBeEmpty($"no interface declarations were found under {typesDirectory}");
        return parsed;
    }

    private void Parse(string[] lines)
    {
        string? current = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            if (line.StartsWith("export interface ", StringComparison.Ordinal))
            {
                var header = line["export interface ".Length..].TrimEnd('{', ' ');
                var parts = header.Split(" extends ", StringSplitOptions.TrimEntries);

                current = parts[0];
                _interfaces[current] = (parts.Length > 1 ? parts[1] : null,
                                        new Dictionary<string, bool>(StringComparer.Ordinal));
                continue;
            }

            if (current is null) continue;

            if (line.StartsWith('}'))
            {
                current = null;
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;

            var name = line[..colon];
            var optional = name.EndsWith('?');

            _interfaces[current].Keys[optional ? name[..^1] : name] = optional;
        }
    }

    /// <summary>The repository root, found by the solution file: the tests run out of a nested bin directory.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EverTask.slnx")))
            directory = directory.Parent;

        directory.ShouldNotBeNull("the dashboard's type declarations are read from the working tree");
        return directory.FullName;
    }
}
