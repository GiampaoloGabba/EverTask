using EverTask.Serialization;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// First-class declarative-polymorphism support, proven END-TO-END: a task whose payload carries a nested
/// polymorphic property (declarative <c>[JsonDerivedType]</c> discriminator) is persisted, then recovered by
/// the real startup flow and executed — the handler must receive the CORRECT concrete subtype with its
/// members. This exercises the full serialize → store → deserialize → execute chain (a normal immediate
/// dispatch would skip the deserialize, so recovery is the meaningful path).
/// </summary>
public class PolymorphicPayloadRecoveryIntegrationTests : IsolatedIntegrationTestBase
{
    private async Task<IHost> CreateMemoryHostWithStateAsync(ResilienceTestState state, bool startHost) =>
        await CreateIsolatedHostWithBuilderAsync(
            builder =>
            {
                builder.AddMemoryStorage();
                builder.Services.AddSingleton(state);
            },
            startHost: startHost);

    [Fact]
    public async Task Recovers_and_executes_task_with_polymorphic_payload_on_correct_subtype()
    {
        var state = new ResilienceTestState();
        await CreateMemoryHostWithStateAsync(state, startHost: false);

        var task = new PolymorphicNotifyTask(new EmailChannel { Address = "ops@evertask.dev" });

        // Persist the row exactly as the dispatcher would (EverTaskJson with the declarative discriminator).
        await Storage.Persist(new QueuedTask
        {
            Id           = Guid.NewGuid(),
            Type         = task.GetType().AssemblyQualifiedName!,
            Request      = EverTaskJson.Serialize(task),
            Handler      = "seeded-by-test",
            Status       = QueuedTaskStatus.Queued,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        await Host!.StartAsync();

        await TaskWaitHelper.WaitForConditionAsync(() => state.CapturedPayloads.Count >= 1, timeoutMs: 10000);

        // The concrete EmailChannel (not the abstract base) and its Address survived persist → recovery.
        state.CapturedPayloads.ShouldContain("email:ops@evertask.dev");
    }

    /// <summary>
    /// P2-5: the closed-set guarantee proven through the REAL recovery deserializer. A persisted payload with a
    /// discriminator OUTSIDE the declared <c>[JsonDerivedType]</c> set ("$kind":"evil") must NOT load an
    /// arbitrary type and must NOT execute the handler — STJ rejects the unknown discriminator on read, the row
    /// goes down the bounded "unusable payload" path, and the handler never runs.
    /// </summary>
    [Fact]
    public async Task Recovery_does_not_execute_payload_with_hostile_discriminator()
    {
        var state = new ResilienceTestState();
        await CreateMemoryHostWithStateAsync(state, startHost: false);

        // A real, loadable IEverTask type, but a payload whose nested $kind is not in the closed derived set.
        await Storage.Persist(new QueuedTask
        {
            Id           = Guid.NewGuid(),
            Type         = typeof(PolymorphicNotifyTask).AssemblyQualifiedName!,
            Request      = "{\"Channel\":{\"$kind\":\"evil\",\"Address\":\"attacker@evil.example\"}}",
            Handler      = "seeded-by-test",
            Status       = QueuedTaskStatus.Queued,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        // Sentinel proves recovery actually ran (no sleep).
        await Storage.Persist(new QueuedTask
        {
            Id           = Guid.NewGuid(),
            Type         = new ResilienceCounterTask(909).GetType().AssemblyQualifiedName!,
            Request      = EverTaskJson.Serialize(new ResilienceCounterTask(909)),
            Handler      = "seeded-by-test",
            Status       = QueuedTaskStatus.Queued,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        await Host!.StartAsync();

        await TaskWaitHelper.WaitForConditionAsync(() => state.ExecutedIndexes.Contains(909), timeoutMs: 10000);

        state.CapturedPayloads.ShouldBeEmpty(
            "a payload with a discriminator outside the closed derived-type set must NOT execute the handler (P2-5)");
    }

    /// <summary>
    /// Gap #7: a persisted Type that resolves to a REAL type which is NOT an <see cref="IEverTask"/> must be
    /// rejected by the <c>IsAssignableFrom</c> guard and never executed — the closed-set protection is on the
    /// type as well as the discriminator.
    /// </summary>
    [Fact]
    public async Task Recovery_does_not_execute_row_whose_type_is_a_real_non_IEverTask_type()
    {
        var state = new ResilienceTestState();
        await CreateMemoryHostWithStateAsync(state, startHost: false);

        await Storage.Persist(new QueuedTask
        {
            Id           = Guid.NewGuid(),
            Type         = typeof(System.Text.StringBuilder).AssemblyQualifiedName!, // real, loadable, NOT IEverTask
            Request      = "{}",
            Handler      = "seeded-by-test",
            Status       = QueuedTaskStatus.Queued,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        await Storage.Persist(new QueuedTask
        {
            Id           = Guid.NewGuid(),
            Type         = new ResilienceCounterTask(910).GetType().AssemblyQualifiedName!,
            Request      = EverTaskJson.Serialize(new ResilienceCounterTask(910)),
            Handler      = "seeded-by-test",
            Status       = QueuedTaskStatus.Queued,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        await Host!.StartAsync();

        await TaskWaitHelper.WaitForConditionAsync(() => state.ExecutedIndexes.Contains(910), timeoutMs: 10000);

        state.CapturedPayloads.ShouldBeEmpty(
            "a non-IEverTask type must be rejected by the IsAssignableFrom guard and never executed (gap #7)");
    }
}
