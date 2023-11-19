using System.Text;

namespace EverTask;

public static class ExceptionExtensions
{
    public static string? ToDetailedString(this Exception? exception)
    {
        if (exception == null) return null;

        var stringBuilder = new StringBuilder();

        AppendExceptionDetails(stringBuilder, exception, 0);

        return stringBuilder.ToString();
    }

    private static void AppendExceptionDetails(StringBuilder stringBuilder, Exception exception, int level)
    {
        string indent = new string(' ', level * 4);

        stringBuilder.AppendLine($"{indent}Exception Type: {exception.GetType().FullName}");
        stringBuilder.AppendLine($"{indent}Message: {exception.Message}");
        stringBuilder.AppendLine($"{indent}Stack Trace: {exception.StackTrace}");

        if (exception.Data.Count > 0)
        {
            stringBuilder.AppendLine($"{indent}Data:");
            foreach (var key in exception.Data.Keys)
            {
                stringBuilder.AppendLine($"{indent}    {key}: {exception.Data[key]}");
            }
        }

        if (exception is AggregateException aggregateException)
        {
            foreach (var innerException in aggregateException.InnerExceptions)
            {
                stringBuilder.AppendLine($"{indent}Aggregate Inner Exception:");
                AppendExceptionDetails(stringBuilder, innerException, level + 1);
            }
        }
        else if (exception.InnerException != null)
        {
            stringBuilder.AppendLine($"{indent}Inner Exception:");
            AppendExceptionDetails(stringBuilder, exception.InnerException, level + 1);
        }
    }
}
