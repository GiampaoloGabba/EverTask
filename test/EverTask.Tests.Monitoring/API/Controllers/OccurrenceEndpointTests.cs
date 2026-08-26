using EverTask.Tests.Monitoring.TestData;
using EverTask.Tests.Monitoring.TestHelpers;

namespace EverTask.Tests.Monitoring.API.Controllers;

/// <summary>
/// What the monitoring API says about durable occurrences, over a real host: the schedules and the occurrence
/// rows under test are produced by the real dispatcher and the real materializer, so what the endpoints report
/// is what the library actually wrote — not a shape a test invented for them.
/// </summary>
/// <remarks>
/// Every replay is bounded by a run budget instead of a stopwatch: a catch-up left to run free keeps
/// materializing while the assertions read, and "how many occurrences are there" would have no stable answer.
/// </remarks>
public class OccurrenceEndpointTests : MonitoringTestBase
{
    private const int BacklogMinutes = 6;
    private const int Replayed       = 4;

    protected override bool EnableWorker => true;

    private ITaskDispatcher Dispatcher => Factory.Services.GetRequiredService<ITaskDispatcher>();

    /// <summary>
    /// A durable minute grid owing the last few minutes, allowed exactly <paramref name="maxRuns"/> of them:
    /// the backfill puts the cursor in the past, so the first materializer run has real missed work to replay,
    /// and the run budget closes the series at a known size.
    /// </summary>
    private async Task<Guid> DispatchCatchUpAsync(int minutesBehind = BacklogMinutes, int maxOccurrences = 50,
                                                  int? maxRuns = Replayed)
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-minutesBehind);

        return await Dispatcher.Dispatch(new SampleTask("catch-up"), r =>
        {
            var schedule = r.Schedule().EveryMinute()
                            .OnMisfire(m => m.CatchUp(
                                new CatchUpOptions(TimeSpan.FromHours(6), maxOccurrences)
                                {
                                    MaxPendingOccurrences = 10
                                }))
                            .BackfillFrom(start);

            if (maxRuns is { } runs)
                schedule.MaxRuns(runs);
        });
    }

    private Task<QueuedTask[]> OccurrencesOfAsync(Guid scheduleId) =>
        Storage.Get(t => t.ParentTaskId == scheduleId);

    private async Task<QueuedTask[]> WaitForReplayAsync(Guid scheduleId, int count = Replayed,
                                                        int timeoutMs = 30000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var rows = await OccurrencesOfAsync(scheduleId);
            if (rows.Length >= count)
                return rows;

            await Task.Delay(50);
        }

        var last = await OccurrencesOfAsync(scheduleId);
        last.Length.ShouldBe(count, "the run budget owes exactly this many occurrences");
        return last;
    }

    [Fact]
    public async Task Should_list_the_occurrences_a_schedule_really_materialized()
    {
        var scheduleId = await DispatchCatchUpAsync();
        var rows       = await WaitForReplayAsync(scheduleId);

        var response = await Client.GetAsync($"/evertask-monitoring/api/tasks/{scheduleId}/occurrences");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var page = await DeserializeResponseAsync<OccurrencesResponse>(response);
        page.ShouldNotBeNull();
        page.TotalCount.ShouldBe(Replayed);
        page.Occurrences.Select(o => o.Id).ShouldBe(rows.Select(r => r.Id), ignoreOrder: true);

        page.Occurrences.ShouldAllBe(o => o.ParentTaskId == scheduleId);
        page.Occurrences.ShouldAllBe(o => o.Occurrence.SlotUtc != null);
        page.Occurrences.Select(o => o.Occurrence.RunNumber ?? 0).OrderBy(n => n).ToArray()
            .ShouldBe([1, 2, 3, 4], "each occurrence knows which run of the series it is");

        page.Occurrences.Select(o => o.Occurrence.SlotUtc)
            .ShouldBe(page.Occurrences.Select(o => o.Occurrence.SlotUtc).OrderByDescending(s => s),
                "the list is newest slot first, like the audit endpoints");

        var replayed = page.Occurrences.Where(o => o.Occurrence.MisfireKind == MisfireKind.CatchUp).ToList();
        replayed.ShouldNotBeEmpty("a slot the backfill left behind is a replayed one");
        replayed.ShouldAllBe(o => o.Occurrence.MissedFromUtc != null && o.Occurrence.MissedCount > 0);
    }

    [Fact]
    public async Task Should_page_the_occurrences_it_lists()
    {
        var scheduleId = await DispatchCatchUpAsync();
        await WaitForReplayAsync(scheduleId);

        var page = await DeserializeResponseAsync<OccurrencesResponse>(
            await Client.GetAsync($"/evertask-monitoring/api/tasks/{scheduleId}/occurrences?skip=1&take=2"));

        page.ShouldNotBeNull();
        page.TotalCount.ShouldBe(Replayed, "the total is the whole series, not the page");
        page.Occurrences.Count.ShouldBe(2);
        page.Skip.ShouldBe(1);
        page.Take.ShouldBe(2);
    }

    [Fact]
    public async Task Should_report_the_schedule_an_occurrence_belongs_to_on_its_own_detail()
    {
        var scheduleId = await DispatchCatchUpAsync();
        var rows       = await WaitForReplayAsync(scheduleId);
        var oldest     = rows.OrderBy(r => r.ScheduledExecutionUtc).First();

        var detail = await DeserializeResponseAsync<TaskDetailDto>(
            await Client.GetAsync($"/evertask-monitoring/api/tasks/{oldest.Id}"));

        detail.ShouldNotBeNull();
        detail.ParentTaskId.ShouldBe(scheduleId);
        detail.OccurrenceMode.ShouldBe(OccurrenceMode.Durable);
        detail.NominalSlotUtc.ShouldBe(oldest.ScheduledExecutionUtc);
        detail.Occurrence.ShouldNotBeNull();
        detail.Occurrence.RunNumber.ShouldBe(1, "the oldest replayed slot is the first run of the series");
        detail.Occurrence.MisfireKind.ShouldBe(MisfireKind.CatchUp);
        (detail.Occurrence.MissedCount ?? 0).ShouldBeGreaterThan(1);
        detail.Halt.ShouldBeNull("an occurrence never carries a schedule's runtime state");
    }

    [Fact]
    public async Task Should_report_the_definition_a_durable_schedule_row_carries()
    {
        var scheduleId = await DispatchCatchUpAsync();
        await WaitForReplayAsync(scheduleId);

        var detail = await DeserializeResponseAsync<TaskDetailDto>(
            await Client.GetAsync($"/evertask-monitoring/api/tasks/{scheduleId}"));

        detail.ShouldNotBeNull();
        detail.ParentTaskId.ShouldBeNull("a schedule row is nobody's occurrence");
        detail.OccurrenceMode.ShouldBe(OccurrenceMode.Durable);
        detail.MisfirePolicy.ShouldBe(MisfirePolicy.CatchUp);
        detail.Occurrence.ShouldBeNull();
    }

    [Fact]
    public async Task Should_report_an_ordinary_task_as_belonging_to_no_schedule()
    {
        var taskId = await Dispatcher.Dispatch(new SampleTask("plain"));

        var detail = await DeserializeResponseAsync<TaskDetailDto>(
            await Client.GetAsync($"/evertask-monitoring/api/tasks/{taskId}"));

        detail.ShouldNotBeNull();
        detail.ParentTaskId.ShouldBeNull();
        detail.OccurrenceMode.ShouldBeNull();
        detail.MisfirePolicy.ShouldBeNull();
        detail.Occurrence.ShouldBeNull();
        detail.Halt.ShouldBeNull();
        detail.ScheduleVersion.ShouldBeNull(
            "a task that belongs to no schedule carries no version of one: the schedule fields are absent " +
            "together, which is what a consumer tells them apart by");
    }

    [Fact]
    public async Task Should_report_the_schedule_version_on_the_rows_that_belong_to_a_schedule()
    {
        var scheduleId = await DispatchCatchUpAsync();
        var rows       = await WaitForReplayAsync(scheduleId);

        var schedule = await DeserializeResponseAsync<TaskDetailDto>(
            await Client.GetAsync($"/evertask-monitoring/api/tasks/{scheduleId}"));

        schedule.ShouldNotBeNull();
        schedule.ScheduleVersion.ShouldBe(0, "a schedule nobody has rescheduled is at version 0, not absent");

        var occurrence = await DeserializeResponseAsync<TaskDetailDto>(
            await Client.GetAsync($"/evertask-monitoring/api/tasks/{rows[0].Id}"));

        occurrence.ShouldNotBeNull();
        occurrence.ScheduleVersion.ShouldBe(0, "an occurrence carries the version it was materialized against");
    }

    [Fact]
    public async Task Should_report_when_an_occurrence_started_apart_from_when_it_ended()
    {
        // The two terms a "late" badge can be built from, over a delivery that is PUNCTUAL and SLOW: it
        // starts on its own slot and takes a second and a half to run. Measuring from lastExecutionUtc — the
        // column storage writes on the terminal transition — reports that second and a half as tardiness.
        var scheduleId = await Dispatcher.Dispatch(new SlowSampleTask("punctual but slow"), r =>
        {
            var schedule = r.Schedule().Every(1).Seconds().WithDurableOccurrences();
            schedule.MaxRuns(1);
        });

        await WaitForReplayAsync(scheduleId, count: 1);

        var finished = await WaitForOccurrenceStatusAsync(scheduleId, QueuedTaskStatus.Completed);

        var page = await DeserializeResponseAsync<OccurrencesResponse>(
            await Client.GetAsync($"/evertask-monitoring/api/tasks/{scheduleId}/occurrences"));

        page.ShouldNotBeNull();
        var occurrence = page.Occurrences.ShouldHaveSingleItem();

        occurrence.StartedAtUtc.ShouldNotBeNull("the run really happened, so it has a start");
        occurrence.LastExecutionUtc.ShouldNotBeNull();
        occurrence.Occurrence.SlotUtc.ShouldNotBeNull();

        var slot    = occurrence.Occurrence.SlotUtc.Value;
        var started = occurrence.StartedAtUtc.Value;
        var ended   = occurrence.LastExecutionUtc.Value;

        (ended - started).ShouldBeGreaterThan(TimeSpan.FromSeconds(2),
            "the premise: the handler of this occurrence really took a while");

        (started - slot).ShouldBeLessThan(TimeSpan.FromSeconds(1.5),
            "the occurrence started on its nominal slot, so its lateness is nothing worth a badge");

        (ended - slot).ShouldBeGreaterThan(TimeSpan.FromSeconds(2),
            "and the end of the run is exactly what would have been reported as lateness instead");

        // Same row, same answer on every surface that shows the badge.
        var detail = await DeserializeResponseAsync<TaskDetailDto>(
            await Client.GetAsync($"/evertask-monitoring/api/tasks/{finished.Id}"));

        detail.ShouldNotBeNull();
        detail.StartedAtUtc.ShouldBe(started);

        var list = await ListAsync($"parentTaskId={scheduleId}&pageSize=10");
        list.ShouldNotBeNull();
        list.Items.ShouldHaveSingleItem().StartedAtUtc.ShouldBe(started);
    }

    [Fact]
    public async Task Should_report_when_a_run_that_FAILED_started_instead_of_when_it_ended()
    {
        // The failure path writes LastExecutionUtc and leaves ExecutionTimeMs untouched, so the two columns
        // cannot say when the run began: counting a duration of zero back off the end returns the end. This
        // occurrence is PUNCTUAL and takes a while before it throws, so the end is exactly the wrong answer.
        var scheduleId = await Dispatcher.Dispatch(new SlowFailingSampleTask("punctual but doomed"), r =>
        {
            var schedule = r.Schedule().Every(1).Seconds().WithDurableOccurrences();
            schedule.MaxRuns(1);
        });

        await WaitForReplayAsync(scheduleId, count: 1);

        var failed = await WaitForOccurrenceStatusAsync(scheduleId, QueuedTaskStatus.Failed);

        failed.ExecutionTimeMs.ShouldBe(0,
            "the premise: a failure records no duration, so nothing can be counted back off the end");
        failed.LastExecutionUtc.ShouldNotBeNull("but it does record when the run ended");

        var page = await DeserializeResponseAsync<OccurrencesResponse>(
            await Client.GetAsync($"/evertask-monitoring/api/tasks/{scheduleId}/occurrences"));

        page.ShouldNotBeNull();
        var occurrence = page.Occurrences.ShouldHaveSingleItem();

        occurrence.StartedAtUtc.ShouldNotBeNull("the run really happened, and the audit trail recorded it");
        occurrence.Occurrence.SlotUtc.ShouldNotBeNull();

        var slot    = occurrence.Occurrence.SlotUtc.Value;
        var started = occurrence.StartedAtUtc.Value;
        var ended   = failed.LastExecutionUtc!.Value;

        (ended - started).ShouldBeGreaterThan(TimeSpan.FromSeconds(2),
            "the premise: the handler of this occurrence really ran for a while before it threw");

        (started - slot).ShouldBeLessThan(TimeSpan.FromSeconds(1.5),
            "the occurrence started on its nominal slot: a failure is not a late start");
    }

    [Fact]
    public async Task Should_report_when_a_run_still_in_flight_started()
    {
        // A row that is RUNNING has no LastExecutionUtc at all, so the recorded transition is the only source
        // there is — and the moment an operator most needs to know how late a delivery went out.
        var scheduleId = await Dispatcher.Dispatch(new SlowSampleTask("still running"), r =>
        {
            var schedule = r.Schedule().Every(1).Seconds().WithDurableOccurrences();
            schedule.MaxRuns(1);
        });

        await WaitForReplayAsync(scheduleId, count: 1);

        var running = await WaitForOccurrenceStatusAsync(scheduleId, QueuedTaskStatus.InProgress);
        running.LastExecutionUtc.ShouldBeNull("the premise: no column is written until the run ends");

        var page = await DeserializeResponseAsync<OccurrencesResponse>(
            await Client.GetAsync($"/evertask-monitoring/api/tasks/{scheduleId}/occurrences"));

        page.ShouldNotBeNull();
        var occurrence = page.Occurrences.ShouldHaveSingleItem();

        occurrence.Status.ShouldBe(QueuedTaskStatus.InProgress);
        occurrence.StartedAtUtc.ShouldNotBeNull("a run in flight has a start, and it is the recorded one");
        occurrence.StartedAtUtc.Value.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow);

        var detail = await DeserializeResponseAsync<TaskDetailDto>(
            await Client.GetAsync($"/evertask-monitoring/api/tasks/{running.Id}"));

        detail.ShouldNotBeNull();
        detail.StartedAtUtc.ShouldBe(occurrence.StartedAtUtc, "same row, same answer on every surface");
    }

    [Fact]
    public async Task Should_report_no_start_for_an_occurrence_that_was_failed_without_ever_running()
    {
        // The direct-to-Failed path (an occurrence nobody can rebuild is marked Failed without ever reaching
        // InProgress) — the same storage call the materializer makes. The row ends up with an end and no
        // duration, and inventing a start out of them reported the whole wait as a late start.
        var scheduleId = await SeedIdleScheduleAsync();
        var slot       = DateTimeOffset.UtcNow.AddMinutes(-10);

        await SeedOccurrenceAsync(scheduleId, slot, QueuedTaskStatus.WaitingQueue);

        var occurrenceRow = (await OccurrencesOfAsync(scheduleId)).ShouldHaveSingleItem();

        await Storage.SetStatus(occurrenceRow.Id, QueuedTaskStatus.Failed,
            new InvalidOperationException("this occurrence cannot be rebuilt"), AuditLevel.Full);

        var reread = (await OccurrencesOfAsync(scheduleId)).ShouldHaveSingleItem();
        reread.LastExecutionUtc.ShouldNotBeNull("the premise: the terminal transition stamps an end");

        var page = await DeserializeResponseAsync<OccurrencesResponse>(
            await Client.GetAsync($"/evertask-monitoring/api/tasks/{scheduleId}/occurrences"));

        page.ShouldNotBeNull();
        page.Occurrences.ShouldHaveSingleItem().StartedAtUtc.ShouldBeNull(
            "no handler ever started, so there is no start to report");
    }

    [Fact]
    public async Task Should_stop_reporting_a_halt_of_a_schedule_that_was_cancelled()
    {
        var scheduleId = await DispatchCatchUpAsync(minutesBehind: 120, maxOccurrences: 5, maxRuns: null);

        await WaitForHaltAsync(scheduleId);

        await Dispatcher.Cancel(scheduleId);
        await WaitForStatusAsync(scheduleId, QueuedTaskStatus.Cancelled);

        (await Storage.Get(t => t.Id == scheduleId))[0].RuntimeInfo.ShouldNotBeNull(
            "the premise: nothing clears the marker when a series ends — the cancel has nothing left to halt");

        var detail = await DeserializeResponseAsync<TaskDetailDto>(
            await Client.GetAsync($"/evertask-monitoring/api/tasks/{scheduleId}"));

        detail.ShouldNotBeNull();
        detail.Halt.ShouldBeNull(
            "a cancelled series is not a schedule an operator has to resume: the alert block would ask them " +
            "to revive a series they ended on purpose");

        var overview = await DeserializeResponseAsync<OverviewDto>(
            await Client.GetAsync("/evertask-monitoring/api/dashboard/overview"));

        overview.ShouldNotBeNull();
        overview.CatchUpBacklog.HaltedSchedules.ShouldBe(0,
            "the counter the docs call the one to alert on has to be clearable");
    }

    [Fact]
    public async Task Should_report_a_catch_up_that_halted_itself_over_its_cap()
    {
        // Two hours of a minute grid against a cap of five, with no run budget to shrink the replay below it:
        // the breaker is what a real overflow writes.
        var scheduleId = await DispatchCatchUpAsync(minutesBehind: 120, maxOccurrences: 5, maxRuns: null);

        await WaitForHaltAsync(scheduleId);

        var detail = await DeserializeResponseAsync<TaskDetailDto>(
            await Client.GetAsync($"/evertask-monitoring/api/tasks/{scheduleId}"));

        detail.ShouldNotBeNull();
        detail.Halt.ShouldNotBeNull("the schedule stopped itself and says so on its own row");
        detail.Halt.DetectedAtLeast.ShouldBeGreaterThan(5);
        detail.Halt.CursorUtc.ShouldNotBeNull();

        var overview = await DeserializeResponseAsync<OverviewDto>(
            await Client.GetAsync("/evertask-monitoring/api/dashboard/overview"));

        overview.ShouldNotBeNull();
        overview.CatchUpBacklog.HaltedSchedules.ShouldBe(1);

        (await OccurrencesOfAsync(scheduleId)).ShouldBeEmpty("a halted catch-up materializes nothing");
    }

    [Fact]
    public async Task Should_filter_the_task_list_down_to_occurrences_and_to_replayed_ones()
    {
        var scheduleId = await DispatchCatchUpAsync();
        await WaitForReplayAsync(scheduleId);

        var everything = await ListAsync("pageSize=500");
        everything.ShouldNotBeNull();

        var occurrencesOnly = await ListAsync("onlyOccurrences=true&pageSize=500");
        occurrencesOnly.ShouldNotBeNull();
        occurrencesOnly.Items.ShouldAllBe(t => t.ParentTaskId != null);
        occurrencesOnly.TotalCount.ShouldBe(Replayed, "the seeded store holds no other durable schedule");

        var notOccurrences = await ListAsync("onlyOccurrences=false&pageSize=500");
        notOccurrences.ShouldNotBeNull();
        notOccurrences.Items.ShouldAllBe(t => t.ParentTaskId == null);
        (occurrencesOnly.TotalCount + notOccurrences.TotalCount).ShouldBe(everything.TotalCount,
            "the two halves of the filter are complements");

        var byParent = await ListAsync($"parentTaskId={scheduleId}&pageSize=500");
        byParent.ShouldNotBeNull();
        byParent.TotalCount.ShouldBe(Replayed);

        var catchUpOnly = await ListAsync("onlyCatchUp=true&pageSize=500");
        catchUpOnly.ShouldNotBeNull();
        catchUpOnly.Items.ShouldNotBeEmpty();
        catchUpOnly.Items.ShouldAllBe(t =>
            t.MisfireKind == MisfireKind.CatchUp || t.MisfireKind == MisfireKind.FireOnce);

        var notCatchUp = await ListAsync("onlyCatchUp=false&pageSize=500");
        notCatchUp.ShouldNotBeNull();
        notCatchUp.Items.ShouldAllBe(t => t.MisfireKind == null);
        (catchUpOnly.TotalCount + notCatchUp.TotalCount).ShouldBe(everything.TotalCount,
            "a row stands for missed work or it does not");
    }

    [Fact]
    public async Task Should_count_the_occurrences_apart_from_the_tasks_that_are_not_one()
    {
        var before = await DeserializeResponseAsync<TaskCountsDto>(
            await Client.GetAsync("/evertask-monitoring/api/tasks/counts"));

        before.ShouldNotBeNull();
        before.Occurrences.ShouldBe(0, "the seeded store holds no durable schedule");

        var scheduleId = await DispatchCatchUpAsync();
        await WaitForReplayAsync(scheduleId);

        var after = await DeserializeResponseAsync<TaskCountsDto>(
            await Client.GetAsync("/evertask-monitoring/api/tasks/counts"));

        after.ShouldNotBeNull();
        after.Occurrences.ShouldBe(Replayed);
        after.Standard.ShouldBeGreaterThanOrEqualTo(after.Occurrences,
            "an occurrence is a one-shot row, so it is counted inside the standard tasks too");
        after.Recurring.ShouldBe(before.Recurring + 1, "the schedule row is the only recurring row added");
    }

    [Fact]
    public async Task Should_report_the_backlog_by_state_and_how_late_its_oldest_pending_slot_is()
    {
        // The schedule row and its occurrences are written AFTER the host has started, so nothing delivers
        // them: the states under test stay the states they were written in, which is what the tile counts.
        var scheduleId = await SeedIdleScheduleAsync();

        var oldestPending = DateTimeOffset.UtcNow.AddMinutes(-30);
        await SeedOccurrenceAsync(scheduleId, oldestPending, QueuedTaskStatus.Queued);
        await SeedOccurrenceAsync(scheduleId, DateTimeOffset.UtcNow.AddMinutes(-20), QueuedTaskStatus.WaitingQueue);
        await SeedOccurrenceAsync(scheduleId, DateTimeOffset.UtcNow.AddMinutes(-15), QueuedTaskStatus.InProgress);
        await SeedOccurrenceAsync(scheduleId, DateTimeOffset.UtcNow.AddMinutes(-10), QueuedTaskStatus.Failed);
        await SeedOccurrenceAsync(scheduleId, DateTimeOffset.UtcNow.AddMinutes(-5), QueuedTaskStatus.Cancelled);
        await SeedOccurrenceAsync(scheduleId, DateTimeOffset.UtcNow.AddMinutes(-1), QueuedTaskStatus.Completed);

        var overview = await DeserializeResponseAsync<OverviewDto>(
            await Client.GetAsync("/evertask-monitoring/api/dashboard/overview"));

        overview.ShouldNotBeNull();
        var backlog = overview.CatchUpBacklog;

        backlog.Pending.ShouldBe(2);
        backlog.Active.ShouldBe(1);
        backlog.Failed.ShouldBe(1);
        backlog.Skipped.ShouldBe(1, "a cancelled occurrence is one that will never run");
        backlog.Completed.ShouldBe(1);
        backlog.HaltedSchedules.ShouldBe(0);

        backlog.OldestPendingSlotUtc.ShouldNotBeNull();
        backlog.OldestPendingSlotUtc.Value.ShouldBe(oldestPending, TimeSpan.FromSeconds(2));
        backlog.LagSeconds.ShouldBeGreaterThan(29 * 60);
    }

    [Fact]
    public async Task Should_report_no_backlog_at_all_on_a_host_that_runs_no_durable_schedule()
    {
        var overview = await DeserializeResponseAsync<OverviewDto>(
            await Client.GetAsync("/evertask-monitoring/api/dashboard/overview"));

        overview.ShouldNotBeNull();
        overview.CatchUpBacklog.ShouldBe(new CatchUpBacklogDto(0, 0, 0, 0, 0, null, 0, 0));
    }

    private async Task<TasksPagedResponse?> ListAsync(string query) =>
        await DeserializeResponseAsync<TasksPagedResponse>(
            await Client.GetAsync($"/evertask-monitoring/api/tasks?{query}"));

    /// <summary>Waits for an occurrence of the schedule to reach a state, and returns it.</summary>
    private async Task<QueuedTask> WaitForOccurrenceStatusAsync(Guid scheduleId, QueuedTaskStatus status,
                                                                int timeoutMs = 30000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var match = (await OccurrencesOfAsync(scheduleId)).FirstOrDefault(r => r.Status == status);
            if (match != null)
                return match;

            await Task.Delay(50);
        }

        return (await OccurrencesOfAsync(scheduleId)).FirstOrDefault(r => r.Status == status)
               ?? throw new TimeoutException($"no occurrence of {scheduleId} reached {status}");
    }

    private async Task WaitForStatusAsync(Guid taskId, QueuedTaskStatus status, int timeoutMs = 30000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var rows = await Storage.Get(t => t.Id == taskId);
            if (rows is [{ } row] && row.Status == status)
                return;

            await Task.Delay(50);
        }

        (await Storage.Get(t => t.Id == taskId))[0].Status.ShouldBe(status);
    }

    private async Task WaitForHaltAsync(Guid scheduleId, int timeoutMs = 30000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var rows = await Storage.Get(t => t.Id == scheduleId);
            if (rows is [{ RuntimeInfo: not null }])
                return;

            await Task.Delay(50);
        }

        (await Storage.Get(t => t.Id == scheduleId))[0].RuntimeInfo
            .ShouldNotBeNull("the overflowing catch-up should have written its halt");
    }

    /// <summary>A durable schedule row nothing is going to run, so its occurrences stay where they are put.</summary>
    private async Task<Guid> SeedIdleScheduleAsync()
    {
        var id = Guid.NewGuid();

        await Storage.Persist(new QueuedTask
        {
            Id           = id,
            Type         = typeof(SampleTask).AssemblyQualifiedName!,
            Handler      = typeof(SampleTaskHandler).AssemblyQualifiedName!,
            Request      = "{\"Message\":\"idle schedule\"}",
            Status       = QueuedTaskStatus.Queued,
            IsRecurring  = true,
            NextRunUtc   = DateTimeOffset.UtcNow.AddDays(1),
            CreatedAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
            QueueName    = "recurring"
        });

        return id;
    }

    private Task SeedOccurrenceAsync(Guid scheduleId, DateTimeOffset slot, QueuedTaskStatus status) =>
        Storage.Persist(new QueuedTask
        {
            Id                    = Guid.NewGuid(),
            Type                  = typeof(SampleTask).AssemblyQualifiedName!,
            Handler               = typeof(SampleTaskHandler).AssemblyQualifiedName!,
            Request               = "{\"Message\":\"seeded occurrence\"}",
            Status                = status,
            ParentTaskId          = scheduleId,
            ScheduledExecutionUtc = slot,
            CreatedAtUtc          = slot,
            QueueName             = "recurring"
        });
}
