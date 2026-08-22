namespace EverTask.Tests.TestHelpers;

/// <summary>
/// Provides environment detection and adaptive test parameters for CI/coverage scenarios.
/// Helps tests run with tighter constraints locally while being more forgiving on CI.
/// </summary>
public static class TestEnvironment
{
    /// <summary>
    /// Detects if tests are running in a CI environment (GitHub Actions, Azure Pipelines, etc.)
    /// </summary>
    public static bool IsCI =>
        Environment.GetEnvironmentVariable("CI") == "true" ||
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" ||
        Environment.GetEnvironmentVariable("TF_BUILD") == "true";

    /// <summary>
    /// Detects if code coverage is likely being collected.
    /// Currently assumes coverage runs in CI, but could be extended to detect coverage tools.
    /// </summary>
    public static bool IsCoverage => IsCI; // Assume coverage runs in CI

    /// <summary>
    /// Returns the deadline for a polling wait: the MORE GENEROUS of the two values, whatever the
    /// environment.
    /// <para>
    /// These are deadlines, not asserted bounds - every call site polls an observable and returns as
    /// soon as it holds, so a generous value costs a green run nothing and only lengthens a run that
    /// was going to fail anyway. Picking the tighter value off <see cref="IsCI"/> had it backwards:
    /// none of the CI markers is set for the local run of the three target frameworks in parallel,
    /// which is the heaviest load these tests ever see, so it got the shortest deadlines of all.
    /// </para>
    /// <para>
    /// Both parameters are kept so the call sites still read as the local/CI pair they were written as.
    /// </para>
    /// </summary>
    /// <param name="localMs">Timeout in milliseconds sized for a local run</param>
    /// <param name="ciMs">Timeout in milliseconds sized for CI/coverage</param>
    /// <returns>The longer of the two timeouts</returns>
    public static int GetTimeout(int localMs, int ciMs) => Math.Max(localMs, ciMs);

    /// <summary>
    /// Returns an adaptive iteration count based on the environment.
    /// Uses local count for development machines, CI count for CI/coverage scenarios.
    /// </summary>
    /// <param name="local">Iteration count for local development (more iterations for thorough testing)</param>
    /// <param name="ci">Iteration count for CI/coverage (fewer iterations to reduce execution time)</param>
    /// <returns>Appropriate iteration count for current environment</returns>
    public static int GetIterations(int local, int ci) => IsCI ? ci : local;

    /// <summary>
    /// Returns an adaptive cron interval (seconds) based on the environment.
    /// Uses tighter interval locally, more generous interval on CI.
    /// </summary>
    /// <param name="localSeconds">Interval in seconds for local development</param>
    /// <param name="ciSeconds">Interval in seconds for CI/coverage</param>
    /// <returns>Appropriate interval for current environment</returns>
    public static int GetCronInterval(int localSeconds, int ciSeconds) => IsCI ? ciSeconds : localSeconds;

    /// <summary>
    /// Gets a descriptive string of the current environment (for logging/debugging)
    /// </summary>
    public static string EnvironmentDescription => IsCI
        ? $"CI Environment (GITHUB_ACTIONS={Environment.GetEnvironmentVariable("GITHUB_ACTIONS")}, Coverage={IsCoverage})"
        : "Local Development";
}
