namespace EverTask.Abstractions;

/// <summary>
/// Marks a task as rate-limited and carries the throttling key on the task itself.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="RateLimitKey"/> identifies the logical bucket (tenant, account, external
/// resource) whose execution frequency is constrained by the handler's
/// <see cref="IEverTaskHandlerOptions.RateLimitPolicy"/>. Tasks sharing the same key and task
/// type share the same budget; tasks with different keys never block each other.
/// </para>
/// <para>
/// <strong>Important:</strong> the rate-limit key is a <em>throttling</em> key. It is distinct
/// from the dispatch <c>taskKey</c> (idempotency/deduplication): never reuse one for the other.
/// Many tasks typically share the same rate-limit key (e.g. one per tenant), while a
/// <c>taskKey</c> identifies a single logical task.
/// </para>
/// <para>
/// Implementing this interface is the preferred way to declare the key. Alternatively, a handler
/// can override <see cref="IEverTaskHandler{TTask}.GetRateLimitKey"/> to derive the key without
/// touching the task type.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public record SyncTenantData(Guid TenantId) : IEverTask, IRateLimitedTask
/// {
///     public string RateLimitKey => TenantId.ToString();
/// }
/// </code>
/// </example>
public interface IRateLimitedTask
{
    /// <summary>
    /// Gets the logical key whose execution frequency is throttled (e.g. a tenant id).
    /// </summary>
    string RateLimitKey { get; }
}
