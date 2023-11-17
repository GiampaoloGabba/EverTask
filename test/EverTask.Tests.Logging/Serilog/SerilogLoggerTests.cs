using EverTask.Serilog;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace EverTask.Tests.Logging.Serilog;

public class SerilogLoggerTests
{
    [Fact]
    public void Should_return_expected_values_for_IsEnabled()
    {
        // Arrange
        var logger = new LoggerConfiguration()
                     .MinimumLevel.Information()
                     .CreateLogger();
        var everTaskLogger = new EverTaskSerilogLogger<SerilogLoggerTests>(logger);

        // Act
        var resultForInformation = everTaskLogger.IsEnabled(LogLevel.Information);
        var resultForError       = everTaskLogger.IsEnabled(LogLevel.Error);
        var resultForNone        = everTaskLogger.IsEnabled(LogLevel.None);

        // Assert
        Assert.True(resultForInformation);
        Assert.True(resultForError);
        Assert.False(resultForNone);
    }

    [Fact]
    public void Should_log_correctly()
    {
        // Arrange
        var logger = new LoggerConfiguration()
                     .MinimumLevel.Verbose()
                     .WriteTo.Sink(new DelegateSink(logEvent =>
                     {
                         // Assert inside the sink, to capture the log event
                         Assert.Equal("Test message", logEvent.MessageTemplate.Text);
                         Assert.Equal(LogEventLevel.Error, logEvent.Level);
                     }))
                     .CreateLogger();

        var everTaskLogger = new EverTaskSerilogLogger<SerilogLoggerTests>(logger);

        // Act
        everTaskLogger.Log(LogLevel.Error, new EventId(), "Test message", null, (state, exception) => state.ToString());
    }

    [Fact]
    public void Should_begin_scope_correctly()
    {
        // Arrange
        var logger = new LoggerConfiguration()
                     .MinimumLevel.Verbose()
                     .CreateLogger();

        var everTaskLogger = new EverTaskSerilogLogger<SerilogLoggerTests>(logger);

        // Act
        using var scope = everTaskLogger.BeginScope("Test scope");

        // Assert
        Assert.NotNull(scope);
    }
}

public class DelegateSink : ILogEventSink
{
    private readonly Action<LogEvent> _writeAction;

    public DelegateSink(Action<LogEvent> writeAction)
    {
        _writeAction = writeAction;
    }

    public void Emit(LogEvent logEvent)
    {
        _writeAction(logEvent);
    }
}
