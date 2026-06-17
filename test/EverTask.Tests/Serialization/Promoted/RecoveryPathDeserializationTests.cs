
using EverTask.Serialization;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.Serialization;

/// <summary>
/// Mirrors what <c>WorkerService.ProcessPendingAsync</c> actually does on recovery: resolve the stored
/// assembly-qualified type name with <see cref="Type.GetType(string)"/> and deserialize via the NON-generic
/// <c>Deserialize(json, Type)</c> overload back into <see cref="IEverTask"/>. Plus serialization stability
/// (idempotence), which keeps stored payloads byte-stable across re-writes.
/// </summary>
public class RecoveryPathDeserializationTests
{
    private static ComplexTask SampleTask() => new(
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), 3, 1_000L, 9.5m, 0.25, true,
        new DateTimeOffset(2026, 6, 17, 12, 0, 0, TimeSpan.Zero), TimeSpan.FromMinutes(2),
        PocPriority.Normal, "n", new List<int> { 1 }, new[] { "t" },
        new Dictionary<string, string> { ["k"] = "v" }, new NestedDto("nd", 1) { Flag = false });

    [Fact]
    public void Recovery_resolves_type_by_AQN_and_deserializes_to_IEverTask()
    {
        var task = SampleTask();
        var aqn  = task.GetType().AssemblyQualifiedName!;
        var json = EverTaskJson.Serialize(task);

        // Exactly the recovery sequence.
        var type     = Type.GetType(aqn);
        type.ShouldNotBeNull();
        var restored = EverTaskJson.Deserialize(json, type!);

        restored.ShouldBeOfType<ComplexTask>();
        var ct = (ComplexTask)restored!;
        ct.OrderId.ShouldBe(task.OrderId);
        ct.Amount.ShouldBe(task.Amount);
        (restored is IEverTask).ShouldBeTrue();
    }

    [Fact]
    public void Recovery_deserializes_RecurringTask_by_type()
    {
        var rt   = new RecurringTask { CronInterval = new CronInterval("0 0 * * *"), MaxRuns = 5 };
        var json = EverTaskJson.Serialize(rt);

        var restored = (RecurringTask)EverTaskJson.Deserialize(json, typeof(RecurringTask))!;
        restored.CronInterval!.CronExpression.ShouldBe("0 0 * * *");
        restored.MaxRuns.ShouldBe(5);
    }

    [Fact]
    public void Serialization_is_idempotent_for_payload()
    {
        var task  = SampleTask();
        var once  = EverTaskJson.Serialize(task);
        var twice = EverTaskJson.Serialize(EverTaskJson.Deserialize<ComplexTask>(once)!);
        twice.ShouldBe(once); // stable across round-trips → stored Request stays byte-stable
    }

    [Fact]
    public void Serialization_is_idempotent_for_recurring_task()
    {
        var rt = new RecurringTask
        {
            MonthInterval = new MonthInterval(1, new[] { 1, 6 }) { OnDay = 15, OnTimes = new[] { new TimeOnly(8, 0) } },
            MaxRuns       = 12,
            RunUntil      = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };
        var once  = EverTaskJson.Serialize(rt);
        var twice = EverTaskJson.Serialize(EverTaskJson.Deserialize<RecurringTask>(once)!);
        twice.ShouldBe(once);
    }
}
