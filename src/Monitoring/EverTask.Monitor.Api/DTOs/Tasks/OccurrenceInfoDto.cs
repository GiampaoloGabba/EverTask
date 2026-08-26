using EverTask.Abstractions;

namespace EverTask.Monitor.Api.DTOs.Tasks;

/// <summary>
/// What a materialized occurrence row says about itself: the nominal slot it stands for, the run of the
/// series it is, and the backlog it was created out of when it stands for missed work.
/// </summary>
/// <remarks>
/// Read from the row's runtime metadata, never recomputed: by the time an occurrence is looked at, the
/// backlog it belonged to no longer exists to be measured.
/// </remarks>
/// <param name="SlotUtc">The nominal slot of the occurrence, equal to its scheduled execution time.</param>
/// <param name="RunNumber">The 1-based run of the series this occurrence is.</param>
/// <param name="TimeZoneId">The IANA zone the schedule is read on, or null for plain UTC.</param>
/// <param name="MisfireKind">
/// What kind of missed work this occurrence stands for — a replayed slot, or a whole run of missed slots
/// collapsed into one. Null for an occurrence that is simply its own slot.
/// </param>
/// <param name="MissedFromUtc">The oldest slot of the backlog this occurrence came out of.</param>
/// <param name="MissedThroughUtc">The newest slot of that backlog.</param>
/// <param name="MissedCount">How many grid slots that range holds, both ends included.</param>
/// <param name="MissedCountIsExact">
/// Whether <paramref name="MissedCount"/> is the real total or only a lower bound: a long backlog is not
/// walked to the end just to report a number.
/// </param>
public record OccurrenceInfoDto(
    DateTimeOffset? SlotUtc,
    int? RunNumber,
    string? TimeZoneId,
    MisfireKind? MisfireKind,
    DateTimeOffset? MissedFromUtc,
    DateTimeOffset? MissedThroughUtc,
    int? MissedCount,
    bool? MissedCountIsExact
);
