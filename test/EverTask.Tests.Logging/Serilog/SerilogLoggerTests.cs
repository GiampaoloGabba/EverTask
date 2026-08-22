using EverTask.Logging.Serilog;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Shouldly;
using Xunit;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace EverTask.Tests.Logging.Serilog;

public class SerilogLoggerTests
{
    private static (EverTaskSerilogLogger<SerilogLoggerTests> Logger, List<LogEvent> Events) CreateLogger(
        LogEventLevel minimumLevel = LogEventLevel.Verbose)
    {
        var events = new List<LogEvent>();
        var serilog = new LoggerConfiguration()
                      .MinimumLevel.Is(minimumLevel)
                      .WriteTo.Sink(new DelegateSink(events.Add))
                      .CreateLogger();

        return (new EverTaskSerilogLogger<SerilogLoggerTests>(serilog), events);
    }

    [Fact]
    public void Should_map_levels_and_disable_None_when_checking_IsEnabled()
    {
        var (logger, _) = CreateLogger(LogEventLevel.Information);

        logger.IsEnabled(LogLevel.Trace).ShouldBeFalse();
        logger.IsEnabled(LogLevel.Debug).ShouldBeFalse();
        logger.IsEnabled(LogLevel.Information).ShouldBeTrue();
        logger.IsEnabled(LogLevel.Warning).ShouldBeTrue();
        logger.IsEnabled(LogLevel.Error).ShouldBeTrue();
        logger.IsEnabled(LogLevel.Critical).ShouldBeTrue();
        // The bridge short-circuits None; the old adapter mapped it to Verbose and answered true.
        logger.IsEnabled(LogLevel.None).ShouldBeFalse();
    }

    [Fact]
    public void Should_preserve_template_and_named_properties_when_logging_with_placeholders()
    {
        var (logger, events) = CreateLogger();
        var taskId = Guid.NewGuid();

        logger.LogInformation("Task {TaskId} done in {Ms}ms", taskId, 12);

        var logEvent = events.ShouldHaveSingleItem();
        logEvent.Level.ShouldBe(LogEventLevel.Information);
        logEvent.MessageTemplate.Text.ShouldBe("Task {TaskId} done in {Ms}ms");
        logEvent.Properties["TaskId"].ShouldBeOfType<ScalarValue>().Value.ShouldBe(taskId);
        logEvent.Properties["Ms"].ShouldBeOfType<ScalarValue>().Value.ShouldBe(12);
        logEvent.RenderMessage().ShouldBe($"Task {taskId} done in 12ms");
    }

    [Fact]
    public void Should_preserve_template_properties_and_event_id_when_logging_through_a_generated_method()
    {
        var (logger, events) = CreateLogger();
        var taskId = Guid.NewGuid();

        TestLog.TaskRetried(logger, taskId, 3);

        var logEvent = events.ShouldHaveSingleItem();
        logEvent.Level.ShouldBe(LogEventLevel.Warning);
        logEvent.MessageTemplate.Text.ShouldBe("Task {TaskId} retried {Attempt} times");
        logEvent.Properties["TaskId"].ShouldBeOfType<ScalarValue>().Value.ShouldBe(taskId);
        logEvent.Properties["Attempt"].ShouldBeOfType<ScalarValue>().Value.ShouldBe(3);
        logEvent.RenderMessage().ShouldBe($"Task {taskId} retried 3 times");

        var eventId = logEvent.Properties["EventId"].ShouldBeOfType<StructureValue>();
        eventId.Properties.Single(p => p.Name == "Id").Value.ShouldBeOfType<ScalarValue>().Value.ShouldBe(4242);
        eventId.Properties.Single(p => p.Name == "Name").Value.ShouldBeOfType<ScalarValue>().Value
               .ShouldBe(nameof(TestLog.TaskRetried));
    }

    [Fact]
    public void Should_render_the_state_when_logging_a_plain_string_state()
    {
        var (logger, events) = CreateLogger();

        logger.Log(LogLevel.Error, new EventId(), "Test message", null, (state, _) => state);

        var logEvent = events.ShouldHaveSingleItem();
        logEvent.Level.ShouldBe(LogEventLevel.Error);
        logEvent.RenderMessage().ShouldBe("Test message");
        // A state that is neither a property list nor carries {OriginalFormat} cannot yield a template: the
        // bridge wraps the formatter's output in a single literal-rendered token instead.
        logEvent.MessageTemplate.Text.ShouldBe("{State:l}");
        logEvent.Properties["State"].ShouldBeOfType<ScalarValue>().Value.ShouldBe("Test message");
        // A default EventId (0, null) emits no EventId property.
        logEvent.Properties.ContainsKey("EventId").ShouldBeFalse();
    }

    [Fact]
    public void Should_attach_the_exception_when_logging_an_error()
    {
        var (logger, events) = CreateLogger();
        var exception = new InvalidOperationException("boom");

        logger.LogError(exception, "Task {TaskId} failed", 7);

        var logEvent = events.ShouldHaveSingleItem();
        logEvent.Exception.ShouldBeSameAs(exception);
        logEvent.MessageTemplate.Text.ShouldBe("Task {TaskId} failed");
    }

    [Fact]
    public void Should_set_source_context_to_the_generic_argument_full_name()
    {
        var (logger, events) = CreateLogger();

        logger.LogInformation("anything");

        var logEvent = events.ShouldHaveSingleItem();
        logEvent.Properties[Constants.SourceContextPropertyName].ShouldBeOfType<ScalarValue>().Value
                .ShouldBe(typeof(SerilogLoggerTests).FullName);
    }

    [Fact]
    public void Should_enrich_only_the_events_logged_inside_a_dictionary_scope()
    {
        var (logger, events) = CreateLogger();

        using (logger.BeginScope(new Dictionary<string, object> { ["TaskKey"] = "order-42" }))
        {
            logger.LogInformation("inside");
        }

        logger.LogInformation("outside");

        events.Count.ShouldBe(2);
        events[0].Properties["TaskKey"].ShouldBeOfType<ScalarValue>().Value.ShouldBe("order-42");
        events[1].Properties.ContainsKey("TaskKey").ShouldBeFalse();
    }

    [Fact]
    public void Should_return_a_scope_when_beginning_a_string_scope()
    {
        var (logger, _) = CreateLogger();

        using var scope = logger.BeginScope("Test scope");

        scope.ShouldNotBeNull();
    }
}

internal static partial class TestLog
{
    [LoggerMessage(EventId = 4242, Level = LogLevel.Warning, Message = "Task {TaskId} retried {Attempt} times")]
    public static partial void TaskRetried(ILogger logger, Guid taskId, int attempt);
}

public class DelegateSink(Action<LogEvent> writeAction) : ILogEventSink
{
    public void Emit(LogEvent logEvent) => writeAction(logEvent);
}
