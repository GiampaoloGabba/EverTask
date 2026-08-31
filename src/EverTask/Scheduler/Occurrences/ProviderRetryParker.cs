namespace EverTask.Scheduler.Occurrences;

internal static class ProviderRetryParker
{
    internal static async ValueTask<ProviderRetryParkOutcome> ParkAsync(
        IScheduler scheduler,
        DateTimeOffset retryAt,
        Func<ValueTask<TaskHandlerExecutor?>> buildExecutorAsync,
        Func<TaskHandlerExecutor, TaskHandlerExecutor> prepareRegistration,
        Action<TaskHandlerExecutor, DateTimeOffset> registrationRefused,
        Action<TaskHandlerExecutor, DateTimeOffset> registrationSucceeded,
        Action<TaskHandlerExecutor?, Exception> registrationFailed,
        Func<Exception, bool>? ignoreFailure = null)
    {
        TaskHandlerExecutor? built = null;

        try
        {
            built = await buildExecutorAsync().ConfigureAwait(false);

            if (built is null)
                return default;

            var registration = prepareRegistration(built);

            if (!scheduler.TrySchedule(registration, retryAt))
            {
                registrationRefused(built, retryAt);
                return default;
            }

            registrationSucceeded(built, retryAt);
            return new ProviderRetryParkOutcome(built, null);
        }
        catch (Exception ex) when (ignoreFailure?.Invoke(ex) == true)
        {
            return default;
        }
        catch (Exception ex)
        {
            registrationFailed(built, ex);
            return new ProviderRetryParkOutcome(built, ex);
        }
    }
}

internal readonly record struct ProviderRetryParkOutcome(TaskHandlerExecutor? Executor, Exception? Failure);
