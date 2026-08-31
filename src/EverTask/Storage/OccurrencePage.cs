namespace EverTask.Storage;

/// <summary>
/// One page of the occurrences of a durable schedule, together with how many there are in total.
/// </summary>
/// <remarks>
/// The total travels with the page because a caller that pages needs it and re-reading the rows to count them
/// would defeat the point of asking for a page at all.
/// </remarks>
/// <param name="Occurrences">The occurrences of this page, newest slot first.</param>
/// <param name="TotalCount">How many occurrences the schedule has that match the request.</param>
public sealed record OccurrencePage(QueuedTask[] Occurrences, int TotalCount);
