using EverTask.Resilience;

namespace EverTask.Abstractions;

/// <summary>
/// Extension methods for configuring retry policies with common transient error patterns.
/// </summary>
public static class RetryPolicyExtensions
{
    /// <summary>
    /// Configures retry policy to handle common transient database exceptions.
    /// Includes: DbException, SqlException, TimeoutException (database-related)
    /// </summary>
    /// <param name="policy">The linear retry policy to configure</param>
    /// <returns>The policy instance for fluent chaining</returns>
    /// <exception cref="ArgumentNullException">Thrown when policy is null</exception>
    /// <example>
    /// <code>
    /// RetryPolicy = new LinearRetryPolicy(5, TimeSpan.FromSeconds(2))
    ///     .HandleTransientDatabaseErrors();
    /// </code>
    /// </example>
    public static LinearRetryPolicy HandleTransientDatabaseErrors(this LinearRetryPolicy policy)
    {
        if (policy == null)
            throw new ArgumentNullException(nameof(policy));

        return policy.Handle(
            typeof(System.Data.Common.DbException),
            typeof(TimeoutException)
        );
    }

    /// <summary>
    /// Configures retry policy to handle common transient network exceptions.
    /// Includes: HttpRequestException, SocketException, WebException, TaskCanceledException
    /// </summary>
    /// <param name="policy">The linear retry policy to configure</param>
    /// <returns>The policy instance for fluent chaining</returns>
    /// <exception cref="ArgumentNullException">Thrown when policy is null</exception>
    /// <example>
    /// <code>
    /// RetryPolicy = new LinearRetryPolicy(3, TimeSpan.FromSeconds(1))
    ///     .HandleTransientNetworkErrors();
    /// </code>
    /// </example>
    public static LinearRetryPolicy HandleTransientNetworkErrors(this LinearRetryPolicy policy)
    {
        if (policy == null)
            throw new ArgumentNullException(nameof(policy));

        return policy.Handle(
            typeof(HttpRequestException),
            typeof(System.Net.Sockets.SocketException),
            typeof(System.Net.WebException),
            typeof(TaskCanceledException)
        );
    }

    /// <summary>
    /// Configures retry policy to handle all common transient errors (Database + Network).
    /// Combines HandleTransientDatabaseErrors() and HandleTransientNetworkErrors().
    /// </summary>
    /// <param name="policy">The linear retry policy to configure</param>
    /// <returns>The policy instance for fluent chaining</returns>
    /// <exception cref="ArgumentNullException">Thrown when policy is null</exception>
    /// <example>
    /// <code>
    /// RetryPolicy = new LinearRetryPolicy(5, TimeSpan.FromSeconds(2))
    ///     .HandleAllTransientErrors();
    /// </code>
    /// </example>
    public static LinearRetryPolicy HandleAllTransientErrors(this LinearRetryPolicy policy)
    {
        if (policy == null)
            throw new ArgumentNullException(nameof(policy));

        return policy
            .HandleTransientDatabaseErrors()
            .HandleTransientNetworkErrors();
    }
}
