using System.Text;

namespace EverTask.Tests;

public class ExceptionExtensionsTests
{
    [Fact]
    public void ToDetailedString_ReturnsCorrectFormatForSingleException()
    {
        var testException = new Exception("Test exception message");
        var detailedString = testException.ToDetailedString();

        var expectedString = new StringBuilder()
            .AppendLine("Exception Type: System.Exception")
            .AppendLine("Message: Test exception message")
            .AppendLine($"Stack Trace: {testException.StackTrace}")
            .ToString();

        Assert.Equal(RemoveWhitespace(expectedString), RemoveWhitespace(detailedString));
    }

    [Fact]
    public void ToDetailedString_ReturnsCorrectFormatForNestedException()
    {
        var innerException = new Exception("Inner exception message");
        var testException = new Exception("Test exception message", innerException);
        var detailedString = testException.ToDetailedString();

        var expectedString = new StringBuilder()
            .AppendLine("Exception Type: System.Exception")
            .AppendLine("Message: Test exception message")
            .AppendLine($"Stack Trace: {testException.StackTrace}")
            .AppendLine("    Inner Exception:")
            .AppendLine("        Exception Type: System.Exception")
            .AppendLine("        Message: Inner exception message")
            .AppendLine($"        Stack Trace: {innerException.StackTrace}")
            .ToString();

        Assert.Equal(RemoveWhitespace(expectedString), RemoveWhitespace(detailedString));
    }

    [Fact]
    public void ToDetailedString_ReturnsCorrectFormatForAggregateException()
    {
        var innerException1 = new Exception("Inner exception 1 message");
        var innerException2 = new Exception("Inner exception 2 message");
        var aggregateException = new AggregateException("Aggregate exception message", innerException1, innerException2);
        var detailedString = aggregateException.ToDetailedString();

        var expectedString = new StringBuilder()
                             .AppendLine("Exception Type: System.AggregateException")
                             .AppendLine("Message: Aggregate exception message (Inner exception 1 message) (Inner exception 2 message)")
                             .AppendLine("Stack Trace: ")
                             .AppendLine("Aggregate Inner Exception:")
                             .AppendLine("    Exception Type: System.Exception")
                             .AppendLine("    Message: Inner exception 1 message")
                             .AppendLine("    Stack Trace: ")
                             .AppendLine("Aggregate Inner Exception:")
                             .AppendLine("    Exception Type: System.Exception")
                             .AppendLine("    Message: Inner exception 2 message")
                             .AppendLine("    Stack Trace: ")
                             .ToString();

        Assert.Equal(RemoveWhitespace(expectedString), RemoveWhitespace(detailedString));
    }

    [Fact]
    public void ToDetailedString_ReturnsNullForNullException()
    {
        Exception? nullException = null;
        var detailedString = nullException.ToDetailedString();

        Assert.Null(detailedString);
    }

    private string RemoveWhitespace(string? input) =>
        new(input?
            .Where(c => !char.IsWhiteSpace(c))
            .ToArray());
}
