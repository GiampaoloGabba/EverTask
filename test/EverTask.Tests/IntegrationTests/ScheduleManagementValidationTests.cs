using EverTask.Logger;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// What <see cref="ITaskScheduleManager"/> refuses before it does anything at all: an argument it cannot use, a
/// storage that lacks the capability THAT CALL needs, and a host with no storage under it.
/// </summary>
/// <remarks>
/// The capability gate is per method and the interface says so in five documents (S1/S2): the three calls that
/// rewrite a schedule row need <c>SupportsScheduleVersioning</c>, a requeue addresses a child row and needs
/// <c>SupportsDurableOccurrences</c>, and a cancel needs neither. One blanket sentence for the whole interface
/// was wrong in both directions, so each branch is pinned on its own here.
/// </remarks>
public class ScheduleManagementValidationTests : IsolatedIntegrationTestBase
{
    private const string ProviderKey              = "probe";
    private const string DeterministicProviderKey = "probe-deterministic";

    private readonly RescheduleRecorder _recorder = new();
    private readonly OccurrenceProviderProbe _probe = new();

    private static CapabilityBlindStorage Blind(bool versioning, bool durable) =>
        new(Mock.Of<IEverTaskLogger<MemoryTaskStorage>>(), versioning, durable);

    private Task<IHost> StartHostAsync(ITaskStorage storage) =>
        CreateIsolatedHostWithBuilderAsync(b =>
        {
            b.Services.AddSingleton(storage);
            b.Services.AddSingleton(_recorder);

            // The manager runs a new definition through the same provider gate a dispatch does, so the host
            // needs the same two providers a dispatch would find: one that promises nothing about answering
            // twice the same way, and one that does.
            b.Services.AddSingleton(_probe);
            b.AddOccurrenceProvider<ProbeOccurrenceProvider>(ProviderKey)
             .AddOccurrenceProvider<DeterministicProbeProvider>(DeterministicProviderKey);
        }, startHost: false);

    private ITaskScheduleManager Manager => Host!.Services.GetRequiredService<ITaskScheduleManager>();

    // ---- Arguments --------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_task_key_is_refused_by_every_call_that_takes_one(string? taskKey)
    {
        // A schedule is addressed by the key it was dispatched with, and a blank one addresses nothing: it
        // would reach storage as a lookup that answers "no such schedule", which reads as a missing schedule
        // rather than as a caller passing an empty string.
        await StartHostAsync(Blind(versioning: true, durable: true));

        (await Should.ThrowAsync<ArgumentException>(() =>
            Manager.Reschedule(taskKey!, r => r.Schedule().Every(1).Hours()))).ParamName.ShouldBe("taskKey");

        (await Should.ThrowAsync<ArgumentException>(() => Manager.ReevaluateSchedule(taskKey!)))
            .ParamName.ShouldBe("taskKey");

        (await Should.ThrowAsync<ArgumentException>(() => Manager.ResumeSchedule(taskKey!)))
            .ParamName.ShouldBe("taskKey");

        (await Should.ThrowAsync<ArgumentException>(() => Manager.CancelSchedule(taskKey!)))
            .ParamName.ShouldBe("taskKey");
    }

    [Fact]
    public async Task A_reschedule_with_no_definition_to_build_is_refused()
    {
        await StartHostAsync(Blind(versioning: true, durable: true));

        (await Should.ThrowAsync<ArgumentNullException>(() => Manager.Reschedule("any-key", null!)))
            .ParamName.ShouldBe("configure");
    }

    [Fact]
    public async Task A_reschedule_mode_that_is_not_a_defined_value_is_refused()
    {
        await StartHostAsync(Blind(versioning: true, durable: true));

        // An undefined enum is not a mode with a default meaning: RecalculateFromNow and RebaseFromCursor
        // decide the cursor in ways that cannot be substituted for one another, so a value that names neither
        // is a caller error and never a silent fall-through to the first one.
        var error = await Should.ThrowAsync<ArgumentException>(() =>
            Manager.Reschedule("any-key", r => r.Schedule().Every(1).Hours(), (RescheduleMode)7));

        error.ParamName.ShouldBe("mode");
        error.Message.ShouldContain("7");
    }

    // ---- The capability each CALL needs -----------------------------------------------------------

    [Fact]
    public async Task The_three_calls_that_rewrite_a_schedule_need_schedule_versioning()
    {
        // Durable occurrences are supported and it changes nothing: what these three do is rewrite the
        // schedule row under a compare-and-swap, and without that they could report success while a run
        // finishing at the same moment overwrote them.
        await StartHostAsync(Blind(versioning: false, durable: true));

        foreach (var call in new Func<Task>[]
                 {
                     () => Manager.Reschedule("any-key", r => r.Schedule().Every(1).Hours()),
                     () => Manager.ReevaluateSchedule("any-key"),
                     () => Manager.ResumeSchedule("any-key")
                 })
        {
            (await Should.ThrowAsync<NotSupportedException>(call))
                .Message.ShouldContain(nameof(ITaskStorage.SupportsScheduleVersioning));
        }
    }

    [Fact]
    public async Task A_requeue_needs_durable_occurrences_and_not_schedule_versioning()
    {
        // The mirror image: versioning is supported and it is the wrong capability. A requeue addresses an
        // occurrence row, which only a durable schedule ever creates.
        await StartHostAsync(Blind(versioning: true, durable: false));

        var error = await Should.ThrowAsync<NotSupportedException>(() =>
            Manager.RequeueFailedOccurrence(Guid.NewGuid()));

        error.Message.ShouldContain(nameof(ITaskStorage.SupportsDurableOccurrences));
    }

    [Fact]
    public async Task A_reschedule_whose_new_definition_is_durable_needs_durable_occurrences_too()
    {
        // The capability a call needs is not decided by the call alone: a reschedule rewrites a schedule row,
        // which is what SupportsScheduleVersioning answers for, but a new definition that opts INTO durable
        // occurrences also asks the store for the atomic materialization behind them. It is refused here for
        // the same reason a dispatch refuses it — there is no half-atomic emulation to degrade to — and it is
        // refused while the caller still holds the call, before anything is written.
        var blind = Blind(versioning: true, durable: false);

        await StartHostAsync(blind);

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("turning-durable"),
            r => r.Schedule().Every(1).Hours(), taskKey: "validation-turn-durable");

        var error = await Should.ThrowAsync<NotSupportedException>(() =>
            Manager.Reschedule("validation-turn-durable",
                r => r.Schedule().Every(1).Hours().WithDurableOccurrences()));

        error.Message.ShouldContain(nameof(ITaskStorage.SupportsDurableOccurrences));

        var row = (await blind.Get(t => t.Id == id))[0];
        row.ScheduleVersion.ShouldBe(0, "the refusal comes before the compare-and-swap, so nothing was written");
        row.RecurringTask!.ShouldNotContain("OccurrenceMode", Case.Insensitive);
    }

    [Fact]
    public async Task An_inline_reschedule_is_accepted_by_the_very_storage_that_refused_the_durable_one()
    {
        // The control for the test above: same store, same schedule, same call — only the durable opt-in is
        // gone. Versioning alone is enough, so what was refused is the definition and not the reschedule.
        var blind = Blind(versioning: true, durable: false);

        await StartHostAsync(blind);

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("staying-inline"),
            r => r.Schedule().Every(1).Hours(), taskKey: "validation-stay-inline");

        var result = await Manager.Reschedule("validation-stay-inline", r => r.Schedule().Every(2).Hours());

        result.ScheduleVersion.ShouldBe(1);
        (await blind.Get(t => t.Id == id))[0].ScheduleVersion.ShouldBe(1);
    }

    [Fact]
    public async Task A_cancel_asks_for_no_capability_beyond_a_registered_storage()
    {
        // The one call on the interface that needs neither: it writes a cancellation, which every storage has
        // always been able to do. Refusing it would leave a store with no capability unable to end a series on
        // purpose — the one thing an operator always has to be able to do.
        var blind = Blind(versioning: false, durable: false);

        await StartHostAsync(blind);

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("cancel-on-a-blind-store"),
            r => r.Schedule().Every(1).Hours(), taskKey: "validation-cancel-blind");

        await Manager.CancelSchedule("validation-cancel-blind");

        (await blind.Get(t => t.Id == id))[0].Status.ShouldBe(QueuedTaskStatus.Cancelled);
    }

    // ---- The provider gate, on the manager's own branch -------------------------------------------

    [Fact]
    public async Task A_reschedule_onto_a_key_no_provider_answers_to_is_refused()
    {
        // The manager builds a definition the same way a dispatch does, and an unregistered key is the same
        // verdict on both: configuration, not transience. It does not heal by waiting, so refusing it while
        // the caller still holds the call is the only place it can be reported — committed instead, it would
        // be a row whose every next-run question fails until somebody poisons it.
        var blind = Blind(versioning: true, durable: true);

        await StartHostAsync(blind);

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("unknown-provider"),
            r => r.Schedule().Every(1).Hours(), taskKey: "validation-unknown-provider");

        var refusal = await Should.ThrowAsync<ArgumentException>(() =>
            Manager.Reschedule("validation-unknown-provider",
                r => r.Schedule().UseOccurrenceProvider("nobody-registers-this")));

        refusal.Message.ShouldContain("nobody-registers-this");
        refusal.Message.ShouldContain(ProviderKey, Case.Sensitive, "the message names what IS registered");

        var row = (await blind.Get(t => t.Id == id))[0];
        row.ScheduleVersion.ShouldBe(0, "the refusal comes before the compare-and-swap, so nothing was written");
        row.RecurringTask!.ShouldNotContain("Provider", Case.Insensitive);
    }

    [Fact]
    public async Task A_reschedule_keeping_only_the_newest_slots_needs_a_provider_that_answers_the_same_way_twice()
    {
        // The other half of the gate, and the one a dispatch pins on its own side: SkipOldest finds where the
        // newest slots of a backlog begin by PROBING the grid at instants it never returned, so a calendar
        // that may answer differently the second time would replay the wrong ones. It is decided here, where
        // the caller is holding the call, and never on the recovery path — reading IsDeterministic means
        // building the provider, and a container hiccup must not poison a series.
        var blind = Blind(versioning: true, durable: true);

        await StartHostAsync(blind);

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("skip-oldest"),
            r => r.Schedule().Every(1).Hours(), taskKey: "validation-skip-oldest");

        var refusal = await Should.ThrowAsync<InvalidOperationException>(() =>
            Manager.Reschedule("validation-skip-oldest",
                r => r.Schedule().UseOccurrenceProvider(ProviderKey).OnMisfire(m => m.CatchUp(KeepNewest))));

        refusal.Message.ShouldContain(nameof(INextOccurrenceProvider.IsDeterministic));

        (await blind.Get(t => t.Id == id))[0].ScheduleVersion
            .ShouldBe(0, "and again nothing was written: the refusal precedes the compare-and-swap");

        // The control, so the refusal is about the PROVIDER and not about the policy: the very same catch-up
        // over the provider that does promise it goes through.
        var result = await Manager.Reschedule("validation-skip-oldest",
            r => r.Schedule().UseOccurrenceProvider(DeterministicProviderKey).OnMisfire(m => m.CatchUp(KeepNewest)));

        result.ScheduleVersion.ShouldBe(1);
        (await blind.Get(t => t.Id == id))[0].RecurringTask!.ShouldContain(DeterministicProviderKey);
    }

    /// <summary>The one catch-up policy that has to probe the grid, and therefore the one that needs a promise.</summary>
    private static CatchUpOptions KeepNewest => new(TimeSpan.FromHours(1), 5)
    {
        OverflowPolicy = CatchUpOverflowPolicy.SkipOldest
    };

    // ---- No storage at all ------------------------------------------------------------------------

    [Fact]
    public async Task Every_call_needs_a_registered_storage()
    {
        // The first sentence of the capability contract: a schedule IS a row, and every one of these calls
        // changes one. Without persistence there is nothing to address, so each refuses instead of quietly
        // doing nothing.
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton(_recorder);
                services.AddEverTask(cfg => cfg.RegisterTasksFromAssembly(typeof(TestTaskRequest).Assembly));
            })
            .Build();

        var manager = host.Services.GetRequiredService<ITaskScheduleManager>();

        foreach (var call in new Func<Task>[]
                 {
                     () => manager.Reschedule("any-key", r => r.Schedule().Every(1).Hours()),
                     () => manager.ReevaluateSchedule("any-key"),
                     () => manager.ResumeSchedule("any-key"),
                     () => manager.RequeueFailedOccurrence(Guid.NewGuid()),
                     () => manager.CancelSchedule("any-key")
                 })
        {
            (await Should.ThrowAsync<NotSupportedException>(call))
                .Message.ShouldContain("Register a storage provider");
        }
    }
}
