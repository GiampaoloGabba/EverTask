namespace EverTask.Storage;

/// <summary>
/// One page of a row's audit trail — <see cref="StatusAudit"/> or <see cref="RunsAudit"/> — together with how
/// many entries the trail holds in total.
/// </summary>
/// <remarks>
/// The total travels with the page for the same reason as <see cref="OccurrencePage"/>'s: a caller that pages
/// needs it, and re-reading the trail to count it would defeat the point of asking for a page. One generic
/// record rather than two identical ones — the two trails differ in what they hold, not in how they page.
/// </remarks>
/// <param name="Audits">The entries of this page, newest first.</param>
/// <param name="TotalCount">How many entries the row's trail holds.</param>
/// <typeparam name="TAudit">The audit entry type.</typeparam>
public sealed record AuditPage<TAudit>(TAudit[] Audits, int TotalCount);
