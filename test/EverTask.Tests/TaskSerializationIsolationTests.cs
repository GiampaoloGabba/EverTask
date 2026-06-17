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
/// L33: EverTask (de)serializes task payloads and recurring metadata through its own private, isolated
/// System.Text.Json options. A host that opens a hostile global Newtonsoft <c>JsonConvert.DefaultSettings</c>
/// (e.g. <c>TypeNameHandling.All</c>, a gadget-deserialization surface) must NOT be able to influence
/// EverTask's persisted task round-trip. STJ never consults the Newtonsoft global and never emits a
/// <c>$type</c> marker, so the migration preserves and strengthens the isolation guarantee.
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

            // The hostile global would inject a "$type" gadget marker into every Newtonsoft-serialized
            // object; EverTask's STJ serializer never honors it for the persisted task payload (L33).
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
        var previous = JsonConvert.DefaultSettings;
        try
        {
            JsonConvert.DefaultSettings =
                () => new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All };

            var task = new SampleIsolationTask(7);

            // Raw JsonConvert honors the hostile global; EverTaskJson (STJ) is isolated from it.
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
