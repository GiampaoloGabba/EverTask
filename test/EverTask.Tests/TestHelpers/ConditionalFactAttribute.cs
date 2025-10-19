namespace EverTask.Tests.TestHelpers;

/// <summary>
/// Custom xUnit Fact attribute that can skip tests based on runtime conditions.
/// </summary>
public class ConditionalFactAttribute : FactAttribute
{
    public ConditionalFactAttribute(params string[] skipConditions)
    {
        foreach (var condition in skipConditions)
        {
            if (ShouldSkip(condition))
            {
                Skip = $"Test skipped due to condition: {condition}";
                return;
            }
        }
    }

    private static bool ShouldSkip(string condition)
    {
        return condition switch
        {
            "NET6_GITHUB" => IsNet6() && IsGitHubActions(),
            "NET6" => IsNet6(),
            "GITHUB" => IsGitHubActions(),
            _ => false
        };
    }

    private static bool IsNet6()
    {
#if NET6_0
        return true;
#else
        return false;
#endif
    }

    private static bool IsGitHubActions()
    {
        var githubActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS");
        var ci = Environment.GetEnvironmentVariable("CI");

        return !string.IsNullOrEmpty(githubActions) ||
               (!string.IsNullOrEmpty(ci) && ci.Equals("true", StringComparison.OrdinalIgnoreCase));
    }
}
