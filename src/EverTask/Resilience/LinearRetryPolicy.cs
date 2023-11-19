namespace EverTask.Resilience;

public class LinearRetryPolicy : IRetryPolicy
{
    private readonly TimeSpan[] _retryDelays;

    public LinearRetryPolicy(int retryCount, TimeSpan retryDelay)
    {
        if (retryCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(retryCount));

        if (retryDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryDelay));

        _retryDelays = Enumerable.Repeat(retryDelay, retryCount).ToArray();
    }

    public LinearRetryPolicy(TimeSpan[] retryDelays)
    {
        ArgumentNullException.ThrowIfNull(retryDelays);

        if (retryDelays.Length == 0)
            throw new ArgumentException("The collection must contain at least one element.", nameof(retryDelays));

        if (retryDelays.Any(delay => delay <= TimeSpan.Zero))
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelays), "All time spans must be greater than zero.");
        }

        _retryDelays = retryDelays;
    }

    public async Task Execute(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var exceptions = new List<Exception>();
        var attempt    = 0;
        foreach (var retryDelay in _retryDelays)
        {
            if (attempt > 0)
                await Task.Delay(retryDelay).ConfigureAwait(false);

            try
            {
                await action().ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }

            attempt++;
        }

        throw new AggregateException(exceptions);
    }
}
