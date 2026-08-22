using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using EverTask.Abstractions;
using EverTask.Resilience;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace EverTask.Tests;

/// <summary>
/// Unit tests for ExponentialRetryPolicy API contract validation.
/// Delay computation is verified through Execute + onRetryCallback (public API);
/// exception filtering is inherited from RetryPolicyBase and re-verified here.
/// </summary>
public class ExponentialRetryPolicyTests
{
    // Test helper: Mock ILogger
    private static ILogger CreateMockLogger() => Mock.Of<ILogger>();

    // Test helper: run Execute until retries are exhausted, capturing the delay of each retry attempt
    private static async Task<List<TimeSpan>> CaptureRetryDelays(ExponentialRetryPolicy policy)
    {
        var delays = new List<TimeSpan>();

        await Assert.ThrowsAsync<AggregateException>(async () =>
        {
            await policy.Execute(
                ct => throw new InvalidOperationException("always fails"),
                CreateMockLogger(),
                default,
                (attempt, ex, delay) =>
                {
                    delays.Add(delay);
                    return ValueTask.CompletedTask;
                });
        });

        return delays;
    }

    #region Group 1: Constructor Validation

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WithInvalidRetryCount_ThrowsArgumentOutOfRangeException(int retryCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ExponentialRetryPolicy(retryCount, TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public void Constructor_WithNonPositiveInitialDelay_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ExponentialRetryPolicy(3, TimeSpan.Zero));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ExponentialRetryPolicy(3, TimeSpan.FromMilliseconds(-1)));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Constructor_WithInvalidBackoffFactor_ThrowsArgumentOutOfRangeException(double backoffFactor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ExponentialRetryPolicy(3, TimeSpan.FromMilliseconds(10), backoffFactor));
    }

    [Fact]
    public void Constructor_WithMaxDelayLowerThanInitialDelay_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ExponentialRetryPolicy(3, TimeSpan.FromMilliseconds(100),
                maxDelay: TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void Constructor_WithValidArguments_DoesNotThrow()
    {
        _ = new ExponentialRetryPolicy(3, TimeSpan.FromMilliseconds(10));
        _ = new ExponentialRetryPolicy(3, TimeSpan.FromMilliseconds(10), backoffFactor: 1.0);
        _ = new ExponentialRetryPolicy(3, TimeSpan.FromMilliseconds(10),
            maxDelay: TimeSpan.FromMilliseconds(10), useJitter: true);
    }

    #endregion

    #region Group 2: Delay Computation

    [Fact]
    public async Task Execute_WithDefaultBackoffFactor_DoublesDelayEachAttempt()
    {
        // 1ms initial, factor 2, 4 retries -> 1, 2, 4, 8 ms
        var policy = new ExponentialRetryPolicy(4, TimeSpan.FromMilliseconds(1));

        var delays = await CaptureRetryDelays(policy);

        delays.ShouldBe(new[]
        {
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(2),
            TimeSpan.FromMilliseconds(4),
            TimeSpan.FromMilliseconds(8)
        });
    }

    [Fact]
    public async Task Execute_WithCustomBackoffFactor_MultipliesDelayEachAttempt()
    {
        // 1ms initial, factor 3, 3 retries -> 1, 3, 9 ms
        var policy = new ExponentialRetryPolicy(3, TimeSpan.FromMilliseconds(1), backoffFactor: 3.0);

        var delays = await CaptureRetryDelays(policy);

        delays.ShouldBe(new[]
        {
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(3),
            TimeSpan.FromMilliseconds(9)
        });
    }

    [Fact]
    public async Task Execute_WithMaxDelay_CapsComputedDelays()
    {
        // 1ms initial, factor 4, cap 8ms, 4 retries -> 1, 4, 8 (16 capped), 8
        var policy = new ExponentialRetryPolicy(4, TimeSpan.FromMilliseconds(1),
            backoffFactor: 4.0, maxDelay: TimeSpan.FromMilliseconds(8));

        var delays = await CaptureRetryDelays(policy);

        delays.ShouldBe(new[]
        {
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(4),
            TimeSpan.FromMilliseconds(8),
            TimeSpan.FromMilliseconds(8)
        });
    }

    [Fact]
    public async Task Execute_WithBackoffFactorOne_BehavesLikeLinearPolicy()
    {
        var policy = new ExponentialRetryPolicy(3, TimeSpan.FromMilliseconds(2), backoffFactor: 1.0);

        var delays = await CaptureRetryDelays(policy);

        delays.ShouldBe(new[]
        {
            TimeSpan.FromMilliseconds(2),
            TimeSpan.FromMilliseconds(2),
            TimeSpan.FromMilliseconds(2)
        });
    }

    [Fact]
    public async Task Execute_WithJitter_KeepsDelaysWithinJitterBounds()
    {
        // Base delay is constant 20ms (factor 1.0); ±20% jitter -> [16, 24] ms
        var policy = new ExponentialRetryPolicy(3, TimeSpan.FromMilliseconds(20),
            backoffFactor: 1.0, useJitter: true);

        var delays = await CaptureRetryDelays(policy);

        delays.Count.ShouldBe(3);
        foreach (var delay in delays)
        {
            delay.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(15));
            delay.ShouldBeLessThanOrEqualTo(TimeSpan.FromMilliseconds(25));
        }
    }

    [Fact]
    public async Task Execute_WithJitterAndMaxDelay_NeverExceedsMaxDelay()
    {
        // Base delay hits the 10ms cap immediately; upward jitter must be clamped to the cap
        var policy = new ExponentialRetryPolicy(4, TimeSpan.FromMilliseconds(10),
            backoffFactor: 2.0, maxDelay: TimeSpan.FromMilliseconds(10), useJitter: true);

        var delays = await CaptureRetryDelays(policy);

        delays.Count.ShouldBe(4);
        foreach (var delay in delays)
        {
            delay.ShouldBeLessThanOrEqualTo(TimeSpan.FromMilliseconds(10));
            delay.ShouldBeGreaterThan(TimeSpan.Zero);
        }
    }

    #endregion

    #region Group 3: Inherited ShouldRetry Behavior

    [Theory]
    [InlineData(typeof(Exception), true)]
    [InlineData(typeof(InvalidOperationException), true)]
    [InlineData(typeof(HttpRequestException), true)]
    [InlineData(typeof(OperationCanceledException), false)]
    [InlineData(typeof(TimeoutException), false)]
    public void ShouldRetry_DefaultImplementation_ReturnsExpectedResult(Type exceptionType, bool expectedRetry)
    {
        var policy    = new ExponentialRetryPolicy(3, TimeSpan.FromMilliseconds(10));
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        policy.ShouldRetry(exception).ShouldBe(expectedRetry);
    }

    [Fact]
    public void Handle_WithWhitelist_OnlyRetriesConfiguredExceptions()
    {
        var policy = new ExponentialRetryPolicy(3, TimeSpan.FromMilliseconds(10))
            .Handle<HttpRequestException>()
            .Handle<IOException>();

        Assert.True(policy.ShouldRetry(new HttpRequestException()));
        Assert.True(policy.ShouldRetry(new FileNotFoundException())); // Derives from IOException
        Assert.False(policy.ShouldRetry(new InvalidOperationException()));
    }

    [Fact]
    public void DoNotHandle_WithBlacklist_RetriesAllExceptConfigured()
    {
        var policy = new ExponentialRetryPolicy(3, TimeSpan.FromMilliseconds(10))
            .DoNotHandle<ArgumentException>();

        Assert.True(policy.ShouldRetry(new HttpRequestException()));
        Assert.False(policy.ShouldRetry(new ArgumentException()));
        Assert.False(policy.ShouldRetry(new ArgumentNullException())); // Derives from ArgumentException
    }

    [Fact]
    public void HandleWhen_WithPredicate_TakesPrecedenceOverWhitelist()
    {
        var policy = new ExponentialRetryPolicy(3, TimeSpan.FromMilliseconds(10))
            .Handle<HttpRequestException>()
            .HandleWhen(ex => ex.Message.Contains("transient"));

        Assert.False(policy.ShouldRetry(new HttpRequestException("permanent error")));
        Assert.True(policy.ShouldRetry(new InvalidOperationException("transient error")));
    }

    [Fact]
    public void Handle_AndDoNotHandle_ThrowsInvalidOperationException()
    {
        var policy = new ExponentialRetryPolicy(3, TimeSpan.FromMilliseconds(10))
            .Handle<HttpRequestException>();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            policy.DoNotHandle<ArgumentException>());

        Assert.Contains("Cannot use DoNotHandle() after Handle()", ex.Message);
    }

    #endregion

    #region Group 4: Execute Behavior

    [Fact]
    public async Task Execute_WithNonRetryableException_FailsImmediately()
    {
        var policy = new ExponentialRetryPolicy(5, TimeSpan.FromSeconds(1))
            .Handle<HttpRequestException>();

        var attemptCount = 0;

        var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await policy.Execute(
                ct =>
                {
                    attemptCount++;
                    throw new ArgumentException("Not retryable");
                },
                CreateMockLogger());
        });

        Assert.Equal(1, attemptCount); // Only 1 attempt, no retries
        Assert.Equal("Not retryable", ex.Message);
    }

    [Fact]
    public async Task Execute_WithRetryableException_RetriesUntilSuccess()
    {
        var policy = new ExponentialRetryPolicy(5, TimeSpan.FromMilliseconds(1))
            .Handle<HttpRequestException>();

        var attemptCount = 0;

        await policy.Execute(
            ct =>
            {
                attemptCount++;
                if (attemptCount < 3)
                    throw new HttpRequestException("Transient error");
                // Success on attempt 3
                return Task.CompletedTask;
            },
            CreateMockLogger());

        Assert.Equal(3, attemptCount); // 1 initial + 2 retries
    }

    [Fact]
    public async Task Execute_WhenAllRetriesFail_ThrowsAggregateExceptionWithAllAttempts()
    {
        var policy = new ExponentialRetryPolicy(3, TimeSpan.FromMilliseconds(1));

        var attemptCount = 0;

        var aggregate = await Assert.ThrowsAsync<AggregateException>(async () =>
        {
            await policy.Execute(
                ct =>
                {
                    attemptCount++;
                    throw new InvalidOperationException($"failure {attemptCount}");
                },
                CreateMockLogger());
        });

        attemptCount.ShouldBe(4); // 1 initial + 3 retries
        aggregate.InnerExceptions.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Execute_OnRetryCallback_ReceivesOneBasedAttemptNumbers()
    {
        var policy   = new ExponentialRetryPolicy(3, TimeSpan.FromMilliseconds(1));
        var attempts = new List<int>();

        await Assert.ThrowsAsync<AggregateException>(async () =>
        {
            await policy.Execute(
                ct => throw new InvalidOperationException("always fails"),
                CreateMockLogger(),
                default,
                (attempt, ex, delay) =>
                {
                    attempts.Add(attempt);
                    return ValueTask.CompletedTask;
                });
        });

        attempts.ShouldBe(new[] { 1, 2, 3 });
    }

    #endregion

    #region Group 5: Extension Methods

    [Fact]
    public void HandleTransientNetworkErrors_OnExponentialPolicy_RetriesNetworkExceptions()
    {
        var policy = new ExponentialRetryPolicy(3, TimeSpan.FromMilliseconds(10))
            .HandleTransientNetworkErrors();

        Assert.True(policy.ShouldRetry(new HttpRequestException()));
        Assert.True(policy.ShouldRetry(new SocketException()));
        Assert.False(policy.ShouldRetry(new ArgumentException()));
    }

    [Fact]
    public void HandleAllTransientErrors_OnExponentialPolicy_PreservesFluentType()
    {
        // The extension must return ExponentialRetryPolicy (not a base type) for fluent chaining
        ExponentialRetryPolicy policy = new ExponentialRetryPolicy(3, TimeSpan.FromMilliseconds(10))
            .HandleAllTransientErrors();

        Assert.True(policy.ShouldRetry(new HttpRequestException()));
        Assert.False(policy.ShouldRetry(new TimeoutException())); // Always fail-fast
    }

    #endregion
}
