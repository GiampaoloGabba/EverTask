using EverTask.Logger;
using Microsoft.Extensions.Logging;

namespace EverTask.Tests;

public class EverTaskLoggerTests
{
    [Fact]
    public void Should_use_provided_Logger_implementation()
    {
        var serviceProviderMock = new Mock<IServiceProvider>();
        var loggerMock          = new Mock<ILogger<TestTaskHanlder>>();

        serviceProviderMock.Setup(sp => sp.GetService(typeof(ILogger<TestTaskHanlder>)))
                           .Returns(loggerMock.Object);

        var everTaskLogger = new EverTaskLogger<TestTaskHanlder>(serviceProviderMock.Object);

        var evtId = new EventId(1,"Test");
        everTaskLogger.Log(LogLevel.Information, evtId, "Test", null, Formatter);

        loggerMock.Verify(l => l.Log(LogLevel.Information, evtId, "Test", null, Formatter), Times.Once);
        everTaskLogger.IsEnabled(LogLevel.None).ShouldBe(loggerMock.Object.IsEnabled(LogLevel.None));

        everTaskLogger.BeginScope(new Dictionary<string, object?>());
        loggerMock.Verify(l => l.BeginScope(new Dictionary<string, object?>()), Times.Once);

    }

    [Fact]
    public void Should_use_default_Logger_when_no_implementation_provided()
    {
        var serviceProviderMock = new Mock<IServiceProvider>();
        var loggerFactoryMock   = new Mock<ILoggerFactory>();
        var defaultLoggerMock   = new Mock<ILogger<TestTaskHanlder>>();

        loggerFactoryMock.Setup(lf => lf.CreateLogger(It.IsAny<string>()))
                         .Returns(defaultLoggerMock.Object);

        serviceProviderMock.Setup(sp => sp.GetService(typeof(ILogger<TestTaskHanlder>)))
                           .Returns(default(ILogger<TestTaskHanlder>));

        serviceProviderMock.Setup(sp => sp.GetService(typeof(ILoggerFactory)))
                           .Returns(loggerFactoryMock.Object);

        var everTaskLogger = new EverTaskLogger<TestTaskHanlder>(serviceProviderMock.Object);

        var evtId = new EventId(1,"Test");
        everTaskLogger.Log(LogLevel.Information, evtId, "Test", null, Formatter);

        defaultLoggerMock.Verify(l => l.Log(LogLevel.Information, evtId, "Test", null, Formatter), Times.Once);

        everTaskLogger.IsEnabled(LogLevel.None).ShouldBe(defaultLoggerMock.Object.IsEnabled(LogLevel.None));

        everTaskLogger.BeginScope(new Dictionary<string, object?>());
        defaultLoggerMock.Verify(l => l.BeginScope(new Dictionary<string, object?>()), Times.Once);
    }

    private string Formatter(string s, Exception? e) => s;
}
