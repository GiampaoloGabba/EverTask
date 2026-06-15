using EverTask.Abstractions;
using EverTask.Handler;
using EverTask.Serialization;
using Newtonsoft.Json;

namespace EverTask.Tests;

public record SampleIsolationTask(int Value) : IEverTask;

// Mutating the process-global JsonConvert.DefaultSettings must not race other tests' serialization,
// so this collection is not parallelized; the global is set and restored within a tight synchronous
// window inside each test.
[CollectionDefinition("GlobalJsonSettings", DisableParallelization = true)]
public class GlobalJsonSettingsCollection { }

/// <summary>
/// L33: EverTask used parameterless JsonConvert to (de)serialize task payloads and recurring metadata,
/// honoring the process-global JsonConvert.DefaultSettings. A host that sets them (e.g. opening
/// TypeNameHandling globally) could corrupt the recovery round-trip or open a gadget-deserialization
/// surface on recovery. The fix routes all task (de)serialization through explicit, isolated settings
/// (TypeNameHandling.None), so the global no longer affects EverTask.
/// </summary>
[Collection("GlobalJsonSettings")]
public class TaskSerializationIsolationTests
{
    [Fact]
    public void ToQueuedTask_must_not_honor_global_JsonConvert_DefaultSettings()
    {
        var previous = JsonConvert.DefaultSettings;
        try
        {
            JsonConvert.DefaultSettings =
                () => new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All };

            var executor = new TaskHandlerExecutor(
                new SampleIsolationTask(42),
                null,
                typeof(SampleIsolationTask).AssemblyQualifiedName,
                null, null, null, null, null, null,
                Guid.NewGuid(),
                null, null,
                AuditLevel.None);

            var queued = executor.ToQueuedTask();

            // The hostile global would inject a "$type" gadget marker into every serialized object;
            // EverTask must not honor it for the persisted task payload (L33).
            queued.Request.ShouldNotContain("$type");
        }
        finally
        {
            JsonConvert.DefaultSettings = previous;
        }
    }

    [Fact]
    public void EverTaskJson_ignores_global_settings_and_round_trips()
    {
        EverTaskJson.Settings.TypeNameHandling.ShouldBe(TypeNameHandling.None);

        var previous = JsonConvert.DefaultSettings;
        try
        {
            JsonConvert.DefaultSettings =
                () => new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All };

            var task = new SampleIsolationTask(7);

            // Raw JsonConvert honors the hostile global; EverTaskJson is isolated from it.
            JsonConvert.SerializeObject(task).ShouldContain("$type");
            var json = EverTaskJson.Serialize(task);
            json.ShouldNotContain("$type");

            var back = EverTaskJson.Deserialize(json, typeof(SampleIsolationTask)) as SampleIsolationTask;
            back.ShouldNotBeNull();
            back!.Value.ShouldBe(7);
        }
        finally
        {
            JsonConvert.DefaultSettings = previous;
        }
    }
}
