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
}
