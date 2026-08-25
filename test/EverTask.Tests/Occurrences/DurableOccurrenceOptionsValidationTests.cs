using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Builder;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.Occurrences;

/// <summary>
/// Everything the durable-occurrence surface REFUSES. A cap of zero, a window of zero, a callback that picks
/// no policy: each of them is a schedule whose owner believes something the library would then not do, and the
/// only honest moment to say so is the call itself.
/// </summary>
/// <remarks>
/// Pure surface, so pure unit tests: there is no host to build for a constructor that throws. The half that
/// only a persisted row can reach — an enum value outside its defined set, which the tolerant converter passes
/// straight through — is exercised through <see cref="RecurringTask.Validate"/>, the gate every path that
/// accepts a schedule goes through.
/// </remarks>
public class DurableOccurrenceOptionsValidationTests
{
    // ---- CatchUpOptions ----------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_catch_up_age_window_that_is_not_positive_is_refused(int minutes)
    {
        // Zero would drop every missed slot the moment it was counted, which is the opposite of the policy
        // being configured.
        Should.Throw<ArgumentOutOfRangeException>(
                  () => new CatchUpOptions(TimeSpan.FromMinutes(minutes), maxOccurrences: 10))
              .ParamName.ShouldBe("maxAge");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void A_catch_up_episode_cap_below_one_is_refused(int maxOccurrences)
    {
        Should.Throw<ArgumentOutOfRangeException>(
                  () => new CatchUpOptions(TimeSpan.FromHours(1), maxOccurrences))
              .ParamName.ShouldBe(nameof(maxOccurrences));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_concurrency_budget_below_one_is_refused(int maxPending)
    {
        // A schedule allowed no live occurrence at all would never materialize anything again.
        Should.Throw<ArgumentOutOfRangeException>(() => new CatchUpOptions(TimeSpan.FromHours(1), 10)
        {
            MaxPendingOccurrences = maxPending
        });
    }

    [Fact]
    public void An_overflow_policy_outside_its_defined_set_is_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new CatchUpOptions(TimeSpan.FromHours(1), 10)
        {
            OverflowPolicy = (CatchUpOverflowPolicy)42
        });
    }

    [Fact]
    public void The_boundary_values_of_a_catch_up_are_accepted()
    {
        // The other half of every guard above: one tick, one slot, one live occurrence are all legitimate.
        var options = new CatchUpOptions(TimeSpan.FromTicks(1), 1)
        {
            MaxPendingOccurrences = 1,
            OverflowPolicy        = CatchUpOverflowPolicy.SkipOldest
        };

        options.MaxAge.ShouldBe(TimeSpan.FromTicks(1));
        options.MaxOccurrences.ShouldBe(1);
    }

    // ---- FireOnceOptions ---------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_fire_once_age_window_that_is_not_positive_is_refused(int minutes)
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => new FireOnceOptions { MaxAge = TimeSpan.FromMinutes(minutes) });
    }

    [Fact]
    public void A_fire_once_without_an_age_window_fires_however_stale_the_run_is()
    {
        new FireOnceOptions().MaxAge.ShouldBeNull();
        new FireOnceOptions { MaxAge = null }.MaxAge.ShouldBeNull();
    }

    // ---- The OnMisfire callback --------------------------------------------------------------------

    [Fact]
    public void A_misfire_callback_that_picks_no_policy_is_refused()
    {
        // A schedule whose owner believes it has a policy and does not. The callback ran, so the mistake is
        // in the callback: saying so at build time is the only place it is still cheap.
        var builder = new RecurringTaskBuilder();

        var error = Should.Throw<InvalidOperationException>(
            () => builder.Schedule().EveryMinute().OnMisfire(_ => { }));

        error.Message.ShouldContain("no policy");
    }

    [Fact]
    public void A_misfire_callback_that_picks_two_policies_is_refused()
    {
        // Two intentions, only one of which would survive — and which one is an implementation detail.
        var builder = new RecurringTaskBuilder();

        var error = Should.Throw<InvalidOperationException>(
            () => builder.Schedule().EveryMinute().OnMisfire(m =>
            {
                m.Skip();
                m.CatchUp(new CatchUpOptions(TimeSpan.FromHours(1), 10));
            }));

        error.Message.ShouldContain("exactly one misfire policy");
    }

    [Fact]
    public void A_catch_up_without_options_is_refused()
    {
        var builder = new RecurringTaskBuilder();

        Should.Throw<ArgumentNullException>(
            () => builder.Schedule().EveryMinute().OnMisfire(m => m.CatchUp(null!)));
    }

    [Fact]
    public void A_missing_misfire_callback_is_refused()
    {
        var builder = new RecurringTaskBuilder();

        Should.Throw<ArgumentNullException>(() => builder.Schedule().EveryMinute().OnMisfire(null!));
    }

    [Fact]
    public void A_selected_policy_leaves_the_schedule_durable()
    {
        // The control: the guards above refuse mistakes, not the feature.
        var builder = new RecurringTaskBuilder();

        builder.Schedule().EveryMinute().OnMisfire(m => m.CatchUp(new CatchUpOptions(TimeSpan.FromHours(1), 10)));

        builder.RecurringTask.OccurrenceMode.ShouldBe(OccurrenceMode.Durable);
        builder.RecurringTask.Misfire!.Policy.ShouldBe(MisfirePolicy.CatchUp);
    }

    // ---- A row that reaches Validate with a corrupt shape -------------------------------------------

    /// <summary>A durable minute schedule carrying <paramref name="misfire"/>, as a deserialized row would.</summary>
    private static RecurringTask Durable(MisfireSettings misfire) => new()
    {
        MinuteInterval = new MinuteInterval(1),
        OccurrenceMode = OccurrenceMode.Durable,
        Misfire        = misfire
    };

    [Fact]
    public void A_misfire_policy_outside_its_defined_set_poisons_the_row_instead_of_degrading_it()
    {
        // The tolerant enum converter passes an unknown numeric value through rather than failing the whole
        // payload, so this is the gate that catches it. Silently reading it as Skip would run a schedule
        // under a policy nobody chose.
        var schedule = Durable(new MisfireSettings { Policy = (MisfirePolicy)9 });

        Should.Throw<ArgumentException>(schedule.Validate).Message.ShouldContain("MisfirePolicy");
    }

    [Fact]
    public void An_overflow_policy_outside_its_defined_set_poisons_the_row_too()
    {
        var schedule = Durable(new MisfireSettings
        {
            Policy         = MisfirePolicy.CatchUp,
            MaxAge         = TimeSpan.FromHours(1),
            MaxOccurrences = 10,
            OverflowPolicy = (CatchUpOverflowPolicy)9
        });

        Should.Throw<ArgumentException>(schedule.Validate).Message.ShouldContain("CatchUpOverflowPolicy");
    }

    [Fact]
    public void A_persisted_concurrency_budget_below_one_is_refused_as_well()
    {
        var schedule = Durable(new MisfireSettings
        {
            Policy                = MisfirePolicy.CatchUp,
            MaxAge                = TimeSpan.FromHours(1),
            MaxOccurrences        = 10,
            MaxPendingOccurrences = 0
        });

        Should.Throw<ArgumentException>(schedule.Validate).Message.ShouldContain("MaxPendingOccurrences");
    }

    [Fact]
    public void A_catch_up_row_missing_a_cap_is_refused()
    {
        // Both caps are mandatory: without them a long downtime replays an unbounded backlog, which is the
        // one thing durable occurrences may never do.
        Should.Throw<ArgumentException>(Durable(new MisfireSettings
        {
            Policy         = MisfirePolicy.CatchUp,
            MaxOccurrences = 10
        }).Validate);

        Should.Throw<ArgumentException>(Durable(new MisfireSettings
        {
            Policy = MisfirePolicy.CatchUp,
            MaxAge = TimeSpan.FromHours(1)
        }).Validate);
    }

    [Fact]
    public void A_persisted_episode_cap_below_one_is_refused()
    {
        var schedule = Durable(new MisfireSettings
        {
            Policy         = MisfirePolicy.CatchUp,
            MaxAge         = TimeSpan.FromHours(1),
            MaxOccurrences = 0
        });

        Should.Throw<ArgumentException>(schedule.Validate).Message.ShouldContain("MaxOccurrences");
    }

    [Fact]
    public void A_persisted_age_window_that_is_not_positive_is_refused()
    {
        var schedule = Durable(new MisfireSettings
        {
            Policy = MisfirePolicy.FireOnce,
            MaxAge = TimeSpan.Zero
        });

        Should.Throw<ArgumentException>(schedule.Validate).Message.ShouldContain("age window");
    }

    // ---- The two host-wide knobs -------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void A_materialization_budget_below_one_is_refused(int concurrency)
    {
        var configuration = new EverTaskServiceConfiguration();

        Should.Throw<ArgumentOutOfRangeException>(
                  () => configuration.SetMaterializationConcurrency(concurrency))
              .ParamName.ShouldBe(nameof(concurrency));
    }

    [Fact]
    public void A_backlog_retry_interval_shorter_than_the_schedulers_own_tick_is_refused()
    {
        var configuration = new EverTaskServiceConfiguration();

        Should.Throw<ArgumentOutOfRangeException>(
                  () => configuration.SetBacklogRetryInterval(TimeSpan.FromMilliseconds(999)))
              .ParamName.ShouldBe("interval");

        configuration.BacklogRetryInterval.ShouldBe(TimeSpan.FromMinutes(1), "a refused value is not applied");
    }

    [Fact]
    public void A_backlog_retry_interval_longer_than_a_day_is_refused()
    {
        // The upper bound exists because this interval is the LAST resort of a blocked schedule and is added
        // to a UTC instant at every re-park: an interval measured in centuries overflows that addition, and
        // the failure path repeats exactly the same addition, so the schedule ends up parked nowhere at all.
        var configuration = new EverTaskServiceConfiguration();

        Should.Throw<ArgumentOutOfRangeException>(
                  () => configuration.SetBacklogRetryInterval(TimeSpan.FromDays(1).Add(TimeSpan.FromTicks(1))))
              .ParamName.ShouldBe("interval");

        Should.Throw<ArgumentOutOfRangeException>(() => configuration.SetBacklogRetryInterval(TimeSpan.MaxValue));

        configuration.BacklogRetryInterval.ShouldBe(TimeSpan.FromMinutes(1), "a refused value is not applied");
    }

    [Fact]
    public void Both_ends_of_the_backlog_retry_range_are_accepted()
    {
        var configuration = new EverTaskServiceConfiguration();

        configuration.SetBacklogRetryInterval(TimeSpan.FromSeconds(1))
                     .BacklogRetryInterval.ShouldBe(TimeSpan.FromSeconds(1));

        configuration.SetBacklogRetryInterval(TimeSpan.FromDays(1))
                     .BacklogRetryInterval.ShouldBe(TimeSpan.FromDays(1));
    }
}
