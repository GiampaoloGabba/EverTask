using EverTask.Logger;
using Microsoft.Extensions.Logging;

namespace EverTask.Tests;

public class EverTaskLoggerTests
{
    [Fact]
    public void Should_use_provided_Logger_implementation()
    {
        // Arrange
        var serviceProviderMock = new Mock<IServiceProvider>();
        var loggerMock          = new Mock<ILogger<TestTaskHanlder>>();

        serviceProviderMock.Setup(sp => sp.GetService(typeof(ILogger<TestTaskHanlder>)))
                           .Returns(loggerMock.Object);

        var everTaskLogger = new EverTaskLogger<TestTaskHanlder>(serviceProviderMock.Object);

        var evtId = new EventId(1,"Test");

        // Act
        everTaskLogger.Log(LogLevel.Information, evtId, "Test", null, Formatter);

        // Assert
        loggerMock.Verify(l =>
                l.Log(LogLevel.Information, evtId, "Test", null, Formatter),
            Times.Once);
    }

    [Fact]
    public void Should_use_default_Logger_when_no_implementation_provided()
    {
        // Arrange
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

        // Act
        everTaskLogger.Log(LogLevel.Information, evtId, "Test", null, Formatter);

        // Assert
        defaultLoggerMock.Verify(l =>
                l.Log(LogLevel.Information, evtId, "Test", null, Formatter),
            Times.Once);
    }

    private string Formatter(string s, Exception? e) => s;
}
