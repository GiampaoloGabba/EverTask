using EverTask.Scheduler.Occurrences;
using EverTask.Scheduler.Recurring.Builder;

namespace EverTask.Tests.Occurrences;

/// <summary>
/// Everything the occurrence-provider surface REFUSES, and the one thing it deliberately accepts twice.
/// </summary>
/// <remarks>
/// The key is the durable half of the contract — it is what a schedule row carries, and the only thing that
/// turns that row back into an implementation — so every way of naming one badly has to be answered at the
/// call that names it, while the caller is still there to read the answer. Pure surface, so pure unit tests
/// over a real <see cref="ServiceCollection"/>: there is no host to start for a registration that throws.
/// </remarks>
public class OccurrenceProviderRegistrationValidationTests
{
    private const string Key = "business-days";

    private static EverTaskServiceBuilder Builder(IServiceCollection services) =>
        services.AddEverTask(cfg => cfg.RegisterTasksFromAssembly(typeof(ProviderScheduleTask).Assembly));

    // ---- The key AddOccurrenceProvider is given ---------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_provider_registered_under_a_blank_key_is_refused(string? key)
    {
        // A blank key is not a provider nobody uses: it is a provider no schedule can ever name, because the
        // builder refuses the same string. Accepting it here would hide the mistake until the first dispatch.
        Should.Throw<ArgumentException>(
                  () => Builder(new ServiceCollection()).AddOccurrenceProvider<ProbeOccurrenceProvider>(key!))
              .ParamName.ShouldBe("key");
    }

    [Fact]
    public void A_key_already_registered_for_another_provider_is_refused()
    {
        var builder = Builder(new ServiceCollection()).AddOccurrenceProvider<ProbeOccurrenceProvider>(Key);

        // The key is PERSISTED on every schedule that uses it, so it can only ever mean one thing: letting a
        // second registration take it would silently move every one of those rows onto another calendar.
        var refusal = Should.Throw<ArgumentException>(
            () => builder.AddOccurrenceProvider<DeterministicProbeProvider>(Key));

        refusal.ParamName.ShouldBe("key");
        refusal.Message.ShouldContain(nameof(ProbeOccurrenceProvider), Case.Sensitive);
        refusal.Message.ShouldContain(Key);
    }

    [Fact]
    public void Registering_the_same_provider_under_the_same_key_twice_is_a_no_op()
    {
        // The documented promise, and the reason the refusal above is on the TYPE and not on the key alone: a
        // registration that runs on every startup — a module, a feature toggle re-applied, an extension method
        // called from two composition roots — has to stay idempotent.
        var services = new ServiceCollection();
        var builder  = Builder(services);

        builder.AddOccurrenceProvider<ProbeOccurrenceProvider>(Key);

        Should.NotThrow(() => builder.AddOccurrenceProvider<ProbeOccurrenceProvider>(Key));

        services.Count(d => d.ServiceType == typeof(ProbeOccurrenceProvider))
                .ShouldBe(1, "and it leaves one registration behind, not one per call");

        // And the key really is registered after both calls: a schedule naming it validates, which is the gate
        // a dispatch, a reschedule and a recovery all go through.
        services.AddLogging();
        services.AddSingleton<OccurrenceProviderProbe>();

        using var provider = services.BuildServiceProvider();

        var builtByHand = new RecurringTaskBuilder();
        builtByHand.Schedule().UseOccurrenceProvider(Key);

        Should.NotThrow(() => builtByHand.RecurringTask
                                         .Validate(provider.GetRequiredService<OccurrenceProviderRegistry>()));
    }

    // ---- The key the builder is given -------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_schedule_that_names_a_blank_provider_key_is_refused_by_the_builder(string? key)
    {
        // Refused where it is written, not where it is read: a blank key would be persisted on the row and
        // then fail every question the grid is ever asked about it.
        Should.Throw<ArgumentException>(
                  () => new RecurringTaskBuilder().Schedule().UseOccurrenceProvider(key!))
              .ParamName.ShouldBe("key");
    }

    // ---- The host knob ----------------------------------------------------------------------------

    [Fact]
    public void A_provider_retry_configured_with_no_callback_is_refused()
    {
        // Quietly doing nothing would leave the defaults in place while the caller believed it had changed
        // them, which is the one outcome a configuration call must never have.
        Should.Throw<ArgumentNullException>(
                  () => new EverTaskServiceConfiguration().SetOccurrenceProviderRetry(null!))
              .ParamName.ShouldBe("configure");
    }
}
