using EverTask.Storage.EfCore;
using Xunit;

namespace EverTask.Tests.Storage.EfCore;

public class ExceptionExtensionsTests
{
    [Fact]
    public void ToDetailedString_ReturnsNull_WhenExceptionIsNull()
    {
        // Arrange
        Exception? exception = null;

        // Act
        var result = exception.ToDetailedString();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ToDetailedString_ReturnsFormattedString_WhenExceptionIsNotNull()
    {
        // Arrange
        var exception = new Exception("Test exception");

        // Act
        var result = exception.ToDetailedString();

        // Assert
        Assert.NotNull(result);
        Assert.Contains("Exception Type:", result);
        Assert.Contains("Message:", result);
        Assert.Contains("Stack Trace:", result);
    }
}
