using EverTask.Abstractions;
using EverTask.Handler;
using EverTask.Monitoring;

namespace EverTask.Tests.Serialization;

/// <summary>
/// B4 — the monitoring sink (<c>EverTaskEventData.TaskParameters</c>) is a SECOND serialization that flows to
/// an external SignalR consumer. It goes through the SAME isolated <c>EverTaskJson</c> as storage, so the
/// post-migration wire format is: enums as NUMBERS (not string names) and non-ASCII written RAW. This pins
/// that contract and documents the deliberate asymmetry with <c>Monitor.Api</c> (which registers a
/// <c>JsonStringEnumConverter</c> on its HTTP path): the storage/monitoring payload stays numeric for
/// byte-parity with legacy data, independent of the dashboard's HTTP JSON options.
/// </summary>
public class MonitoringWireFormatTests
{
    public enum Severity { Low, Normal, High }

    public record MonitoredTask(Severity Level, string Note) : IEverTask;

    private static EverTaskEventData EventFor(IEverTask task)
    {
        var executor = new TaskHandlerExecutor(
            task, new object(), null, DateTimeOffset.UtcNow, null,
            (_, _) => Task.CompletedTask, null, null, null,
            Guid.NewGuid(), null, null, AuditLevel.Full);

        return EverTaskEventData.FromExecutor(executor, SeverityLevel.Information, "msg", null);
    }

    [Fact]
    public void TaskParameters_writes_enums_as_numbers_not_string_names()
    {
        var data = EventFor(new MonitoredTask(Severity.High, "x"));

        // High == 2: numeric form, NOT "High" — parity with the storage payload and legacy Newtonsoft default.
        data.TaskParameters.ShouldContain("\"Level\":2", Case.Sensitive);
        data.TaskParameters.ShouldNotContain("\"High\"");
    }

    [Fact]
    public void TaskParameters_writes_bmp_non_ascii_raw_and_round_trips_emoji_losslessly()
    {
        const string note = "Caffè 日本語 🚀";
        var data = EventFor(new MonitoredTask(Severity.Low, note));

        // Relaxed encoder → BMP non-ASCII (è, 日本語) stays raw on the monitoring wire (no \uXXXX escaping).
        data.TaskParameters.ShouldContain("Caffè 日本語", Case.Sensitive);
        data.TaskParameters.ShouldNotContain("\\u00E8");

        // Astral-plane characters (4-byte emoji) ARE emitted as an escaped surrogate pair by STJ's relaxed
        // encoder — a benign byte-divergence from Newtonsoft (F12), NOT data loss: the value round-trips.
        var restored = EverTask.Serialization.EverTaskJson.Deserialize<MonitoredTask>(data.TaskParameters)!;
        restored.Note.ShouldBe(note);
    }
}
