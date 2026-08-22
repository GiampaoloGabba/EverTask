using EverTask.Resilience;

namespace EverTask.Abstractions;

/// <summary>
/// Extension methods for configuring retry policies with common transient error patterns.
/// Applies to any delay-based policy deriving from <see cref="RetryPolicyBase{TPolicy}"/>
/// (<see cref="LinearRetryPolicy"/>, <see cref="ExponentialRetryPolicy"/>).
/// </summary>
public static class RetryPolicyExtensions
{
    /// <summary>
    /// Configures retry policy to handle common transient database exceptions.
    /// Includes: DbException, SqlException, TimeoutException (database-related)
    /// </summary>
    /// <param name="policy">The retry policy to configure</param>
    /// <returns>The policy instance for fluent chaining</returns>
    /// <exception cref="ArgumentNullException">Thrown when policy is null</exception>
    /// <example>
    /// <code>
    /// RetryPolicy = new LinearRetryPolicy(5, TimeSpan.FromSeconds(2))
    ///     .HandleTransientDatabaseErrors();
    /// </code>
    /// </example>
    public static TPolicy HandleTransientDatabaseErrors<TPolicy>(this TPolicy policy)
        where TPolicy : RetryPolicyBase<TPolicy>
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
    /// <param name="policy">The retry policy to configure</param>
    /// <returns>The policy instance for fluent chaining</returns>
    /// <exception cref="ArgumentNullException">Thrown when policy is null</exception>
    /// <example>
    /// <code>
    /// RetryPolicy = new ExponentialRetryPolicy(3, TimeSpan.FromSeconds(1))
    ///     .HandleTransientNetworkErrors();
    /// </code>
    /// </example>
    public static TPolicy HandleTransientNetworkErrors<TPolicy>(this TPolicy policy)
        where TPolicy : RetryPolicyBase<TPolicy>
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
    /// <param name="policy">The retry policy to configure</param>
    /// <returns>The policy instance for fluent chaining</returns>
    /// <exception cref="ArgumentNullException">Thrown when policy is null</exception>
    /// <example>
    /// <code>
    /// RetryPolicy = new LinearRetryPolicy(5, TimeSpan.FromSeconds(2))
    ///     .HandleAllTransientErrors();
    /// </code>
    /// </example>
    public static TPolicy HandleAllTransientErrors<TPolicy>(this TPolicy policy)
        where TPolicy : RetryPolicyBase<TPolicy>
    {
        if (policy == null)
            throw new ArgumentNullException(nameof(policy));

        return policy
            .HandleTransientDatabaseErrors()
            .HandleTransientNetworkErrors();
    }
}
