using System.Text.RegularExpressions;

// Moq is a global using of this suite and brings a Match of its own.
using Match = System.Text.RegularExpressions.Match;

namespace EverTask.Tests.Monitoring.TestHelpers;

/// <summary>
/// <c>docs/monitoring-api-reference.md</c>, read from the working tree as the contract it presents itself as.
/// </summary>
/// <remarks>
/// The samples in that file are what a consumer writes its client against, and nothing but a reader like this
/// one ever compares them to the DTOs: the response block of the two audit endpoints documented keys the API
/// has never sent (<c>taskId</c>, <c>oldStatus</c>, <c>changedAtUtc</c>, <c>executionStartedUtc</c>) and the
/// example request of <c>GET /tasks</c> passed a filter the endpoint does not bind, which the endpoint
/// answers by ignoring it.
/// </remarks>
internal sealed class ApiReferenceDoc
{
    private const string JsonFence = "```json";
    private const string BashFence = "```bash";
    private const string Fence     = "```";

    private readonly string[] _lines;

    private ApiReferenceDoc(string[] lines) => _lines = lines;

    public static ApiReferenceDoc Load()
    {
        var path = Path.Combine(RepositoryRoot(), "docs", "monitoring-api-reference.md");
        File.Exists(path).ShouldBeTrue($"the API reference is read from the working tree: {path}");

        return new ApiReferenceDoc(File.ReadAllLines(path));
    }

    /// <summary>The first JSON sample under a heading, parsed.</summary>
    public JsonElement JsonSampleUnder(string heading)
    {
        var (start, end) = SectionOf(heading);
        var block = BlockAfter(JsonFence, start, end);

        block.ShouldNotBeNull($"'{heading}' documents a JSON response");
        return Parse(block, heading);
    }

    /// <summary>The request line of the "Example Request" block under a heading, verb included.</summary>
    public string ExampleRequestUnder(string heading)
    {
        var (start, end) = SectionOf(heading);
        var marker = Array.FindIndex(_lines, start, end - start,
            l => l.Trim() == "**Example Request:**");

        marker.ShouldBeGreaterThanOrEqualTo(0, $"'{heading}' shows an example request");

        var block = BlockAfter(BashFence, marker, end);
        block.ShouldNotBeNull($"the example request of '{heading}' is a bash block");

        return block!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
    }

    /// <summary>Every JSON sample in the file, parsed, with the line it starts on for the failure message.</summary>
    public IEnumerable<(int Line, JsonElement Sample)> JsonSamples()
    {
        for (var i = 0; i < _lines.Length; i++)
        {
            if (_lines[i].Trim() != JsonFence) continue;

            var block = BlockAfter(JsonFence, i, _lines.Length);
            if (block is null) continue;

            yield return (i + 1, Parse(block, $"the JSON sample at line {i + 1}"));
            i += block.Split('\n').Length;
        }
    }

    /// <summary>
    /// The query parameter names every documented call to the task list passes — the URLs of the endpoint
    /// sections and of the language examples alike, since a client is copied from either.
    /// </summary>
    public IEnumerable<(int Line, string Name)> TaskQueryParameterNames()
    {
        // A URL query: everything up to the delimiter that closes it in markdown, a quote or a backtick.
        var urls = new Regex(@"tasks\?([^\s""'`)]+)", RegexOptions.None, TimeSpan.FromSeconds(5));
        // The Python example passes the same parameters as a dict instead of a query string.
        var dict = new Regex(@"params=\{([^}]*)\}", RegexOptions.None, TimeSpan.FromSeconds(5));
        var keys = new Regex(@"'([^']+)'\s*:", RegexOptions.None, TimeSpan.FromSeconds(5));

        for (var i = 0; i < _lines.Length; i++)
        {
            foreach (Match url in urls.Matches(_lines[i]))
            {
                foreach (var pair in url.Groups[1].Value.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var name = pair.Split('=')[0];
                    if (name.Length > 0)
                        yield return (i + 1, name);
                }
            }

            foreach (Match parameters in dict.Matches(_lines[i]))
            {
                foreach (Match key in keys.Matches(parameters.Groups[1].Value))
                    yield return (i + 1, key.Groups[1].Value);
            }
        }
    }

    private (int Start, int End) SectionOf(string heading)
    {
        var start = Array.FindIndex(_lines, l => l.TrimEnd() == heading);
        start.ShouldBeGreaterThanOrEqualTo(0, $"'{heading}' is a heading of the API reference");

        var end = Array.FindIndex(_lines, start + 1,
            l => l.StartsWith("## ", StringComparison.Ordinal) || l.StartsWith("### ", StringComparison.Ordinal));

        return (start, end < 0 ? _lines.Length : end);
    }

    /// <summary>The content of the first fenced block of a kind between two lines, or null.</summary>
    private string? BlockAfter(string fence, int start, int end)
    {
        var open = Array.FindIndex(_lines, start, end - start, l => l.Trim() == fence);
        if (open < 0) return null;

        var close = Array.FindIndex(_lines, open + 1, end - open - 1, l => l.Trim() == Fence);
        if (close < 0) return null;

        return string.Join('\n', _lines[(open + 1)..close]);
    }

    private static JsonElement Parse(string block, string what)
    {
        try
        {
            return JsonDocument.Parse(block).RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new ShouldAssertException($"{what} is not valid JSON: {e.Message}", e);
        }
    }

    /// <summary>The repository root, found by the solution file: the tests run out of a nested bin directory.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EverTask.slnx")))
            directory = directory.Parent;

        directory.ShouldNotBeNull("the API reference is read from the working tree");
        return directory.FullName;
    }
}
