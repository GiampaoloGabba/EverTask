namespace EverTask.RateLimiting;

/// <summary>
/// Dictionary key of a rate-limiter bucket: budgets are scoped per (task type, throttling key),
/// so the same logical key used by two different task types never shares budget.
/// </summary>
internal readonly record struct RateLimiterKey(Type TaskType, string Key);
