using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using EverTask.Resilience;

namespace EverTask.Abstractions;

/// <summary>
/// Extension methods for configuring retry policies with common transient error patterns.
/// Applies to any delay-based policy deriving from <see cref="RetryPolicyBase{TPolicy}"/>
/// (<see cref="LinearRetryPolicy"/>, <see cref="ExponentialRetryPolicy"/>).
/// </summary>
/// <remarks>
/// The non-generic <see cref="LinearRetryPolicy"/> overloads are the original 3.x signatures: they are kept
/// so assemblies compiled against them keep binding, and so a subclass of <see cref="LinearRetryPolicy"/>
/// (which cannot satisfy the self-referencing constraint of the generic overloads) still compiles.
/// Overload resolution prefers them for a <see cref="LinearRetryPolicy"/> receiver; every other policy
/// binds to the generic overload and keeps its concrete type for chaining.
/// </remarks>
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
            typeof(DbException),
            typeof(TimeoutException)
        );
    }

    /// <inheritdoc cref="HandleTransientDatabaseErrors{TPolicy}"/>
    public static LinearRetryPolicy HandleTransientDatabaseErrors(this LinearRetryPolicy policy) =>
        HandleTransientDatabaseErrors<LinearRetryPolicy>(policy);

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
            typeof(SocketException),
            typeof(WebException),
            typeof(TaskCanceledException)
        );
    }

    /// <inheritdoc cref="HandleTransientNetworkErrors{TPolicy}"/>
    public static LinearRetryPolicy HandleTransientNetworkErrors(this LinearRetryPolicy policy) =>
        HandleTransientNetworkErrors<LinearRetryPolicy>(policy);

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

    /// <inheritdoc cref="HandleAllTransientErrors{TPolicy}"/>
    public static LinearRetryPolicy HandleAllTransientErrors(this LinearRetryPolicy policy) =>
        HandleAllTransientErrors<LinearRetryPolicy>(policy);
}
