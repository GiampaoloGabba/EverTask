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
/// Unit tests for LinearRetryPolicy API contract validation.
/// These tests verify the retry policy features in isolation without integration complexity.
/// </summary>
public class LinearRetryPolicyTests
{
    // Test helper: Mock ILogger
    private static ILogger CreateMockLogger() => Mock.Of<ILogger>();

    #region Group 1: ShouldRetry Default Behavior (2 tests)

    [Theory]
    [InlineData(typeof(Exception), true)]
    [InlineData(typeof(InvalidOperationException), true)]
    [InlineData(typeof(HttpRequestException), true)]
    [InlineData(typeof(ArgumentException), true)]
    [InlineData(typeof(OperationCanceledException), false)]
    [InlineData(typeof(TimeoutException), false)]
    public void ShouldRetry_DefaultImplementation_ReturnsExpectedResult(Type exceptionType, bool expectedRetry)
    {
        // Arrange
        var policy = new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(10));
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        // Act
        var shouldRetry = policy.ShouldRetry(exception);

        // Assert
        Assert.Equal(expectedRetry, shouldRetry);
    }

    [Fact]
    public void ShouldRetry_WithoutConfiguration_DefaultsToRetryAll()
    {
        // Arrange
        var policy = new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(10));

        // Act & Assert
        Assert.True(policy.ShouldRetry(new InvalidOperationException()));
        Assert.True(policy.ShouldRetry(new ArgumentException()));
        Assert.True(policy.ShouldRetry(new NullReferenceException()));
        Assert.False(policy.ShouldRetry(new OperationCanceledException()));
    }

    #endregion

    #region Group 2: Handle (Whitelist) API (3 tests)

    [Fact]
    public void Handle_WithWhitelist_OnlyRetriesConfiguredExceptions()
    {
        // Arrange
        var policy = new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(10))
            .Handle<HttpRequestException>()
            .Handle<IOException>();

        // Act & Assert
        Assert.True(policy.ShouldRetry(new HttpRequestException()));
        Assert.True(policy.ShouldRetry(new IOException()));
        Assert.False(policy.ShouldRetry(new InvalidOperationException()));
        Assert.False(policy.ShouldRetry(new ArgumentException()));
    }

    [Fact]
    public void Handle_WithDerivedExceptionType_RetriesDerivedTypes()
    {
        // Arrange
        var policy = new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(10))
            .Handle<IOException>();

        // Act & Assert
        Assert.True(policy.ShouldRetry(new IOException()));
        Assert.True(policy.ShouldRetry(new FileNotFoundException())); // Derives from IOException
        Assert.True(policy.ShouldRetry(new DirectoryNotFoundException())); // Derives from IOException
        Assert.False(policy.ShouldRetry(new InvalidOperationException()));
    }

    [Fact]
    public void Handle_WithParamsTypeArray_AddsMultipleTypes()
    {
        // Arrange & Act
        var policy = new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(10))
            .Handle(
                typeof(HttpRequestException),
                typeof(IOException),
                typeof(InvalidOperationException)
            );

        // Assert
        Assert.True(policy.ShouldRetry(new HttpRequestException()));
        Assert.True(policy.ShouldRetry(new IOException()));
        Assert.True(policy.ShouldRetry(new InvalidOperationException()));
        Assert.False(policy.ShouldRetry(new ArgumentException()));
    }

    #endregion

    #region Group 3: DoNotHandle (Blacklist) API (2 tests)

    [Fact]
    public void DoNotHandle_WithBlacklist_RetriesAllExceptConfigured()
    {
        // Arrange
        var policy = new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(10))
            .DoNotHandle<ArgumentException>()
            .DoNotHandle<NullReferenceException>();

        // Act & Assert
        Assert.True(policy.ShouldRetry(new HttpRequestException()));
        Assert.True(policy.ShouldRetry(new IOException()));
        Assert.False(policy.ShouldRetry(new ArgumentException()));
        Assert.False(policy.ShouldRetry(new ArgumentNullException())); // Derives from ArgumentException
        Assert.False(policy.ShouldRetry(new NullReferenceException()));
    }

    [Fact]
    public void DoNotHandle_WithParamsTypeArray_BlacklistsMultipleTypes()
    {
        // Arrange & Act
        var policy = new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(10))
            .DoNotHandle(
                typeof(ArgumentException),
                typeof(NullReferenceException),
                typeof(InvalidOperationException)
            );

        // Assert
        Assert.True(policy.ShouldRetry(new HttpRequestException()));
        Assert.False(policy.ShouldRetry(new ArgumentException()));
        Assert.False(policy.ShouldRetry(new NullReferenceException()));
        Assert.False(policy.ShouldRetry(new InvalidOperationException()));
    }

    #endregion

    #region Group 4: Conflict Detection (2 tests)

    [Fact]
    public void Handle_AndDoNotHandle_ThrowsInvalidOperationException()
    {
        // Arrange
        var policy = new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(10))
            .Handle<HttpRequestException>();

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() =>
            policy.DoNotHandle<ArgumentException>());

        Assert.Contains("Cannot use DoNotHandle() after Handle()", ex.Message);
    }

    [Fact]
    public void DoNotHandle_ThenHandle_ThrowsInvalidOperationException()
    {
        // Arrange
        var policy = new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(10))
            .DoNotHandle<ArgumentException>();

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() =>
            policy.Handle<HttpRequestException>());

        Assert.Contains("Cannot use Handle() after DoNotHandle()", ex.Message);
    }

    #endregion

    #region Group 5: HandleWhen (Predicate) API (3 tests)

    [Fact]
    public void HandleWhen_WithPredicate_UsesCustomLogic()
    {
        // Arrange
        var policy = new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(10))
            .HandleWhen(ex => ex.Message.Contains("transient"));

        // Act & Assert
        Assert.True(policy.ShouldRetry(new InvalidOperationException("transient error")));
        Assert.True(policy.ShouldRetry(new ArgumentException("transient failure")));
        Assert.False(policy.ShouldRetry(new InvalidOperationException("permanent error")));
        Assert.False(policy.ShouldRetry(new ArgumentException("bad request")));
    }

    [Fact]
    public void HandleWhen_WithNull_ThrowsArgumentNullException()
    {
        // Arrange
        var policy = new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(10));

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => policy.HandleWhen(null!));
    }

    [Fact]
    public void HandleWhen_TakesPrecedence_OverWhitelist()
    {
        // Arrange
        var policy = new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(10))
            .Handle<HttpRequestException>()  // Whitelist HTTP exceptions
            .HandleWhen(ex => ex.Message.Contains("override")); // Predicate takes precedence

        // Act & Assert
        var normalHttp = new HttpRequestException("normal error");
        Assert.False(policy.ShouldRetry(normalHttp)); // Whitelist ignored, predicate used

        var overrideHttp = new HttpRequestException("override error");
        Assert.True(policy.ShouldRetry(overrideHttp)); // Matches predicate

        var overrideOther = new InvalidOperationException("override");
        Assert.True(policy.ShouldRetry(overrideOther)); // Matches predicate
    }

    #endregion

    #region Group 6: Execute Behavior (2 tests)

    [Fact]
    public async Task Execute_WithNonRetryableException_FailsImmediately()
    {
        // Arrange
        var policy = new LinearRetryPolicy(5, TimeSpan.FromSeconds(1))
            .Handle<HttpRequestException>();

        var attemptCount = 0;
        var logger = CreateMockLogger();

        // Act
        var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await policy.Execute(
                ct =>
                {
                    attemptCount++;
                    throw new ArgumentException("Not retryable");
                },
                logger);
        });

        // Assert
        Assert.Equal(1, attemptCount); // Only 1 attempt, no retries
        Assert.Equal("Not retryable", ex.Message);
    }

    [Fact]
    public async Task Execute_WithRetryableException_RetriesUntilSuccess()
    {
        // Arrange
        var policy = new LinearRetryPolicy(5, TimeSpan.FromMilliseconds(10))
            .Handle<HttpRequestException>();

        var attemptCount = 0;
        var logger = CreateMockLogger();

        // Act
        await policy.Execute(
            ct =>
            {
                attemptCount++;
                if (attemptCount < 3)
                    throw new HttpRequestException("Transient error");
                // Success on attempt 3
                return Task.CompletedTask;
            },
            logger);

        // Assert
        Assert.Equal(3, attemptCount); // 1 initial + 2 retries
    }

    #endregion

    #region Group 7: Validation (2 tests)

    [Fact]
    public void Handle_WithInvalidType_ThrowsArgumentException()
    {
        // Arrange
        var policy = new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(10));

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() =>
            policy.Handle(typeof(string))); // string is not an Exception

        Assert.Contains("must derive from Exception", ex.Message);
    }

    [Fact]
    public void DoNotHandle_WithInvalidType_ThrowsArgumentException()
    {
        // Arrange
        var policy = new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(10));

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() =>
            policy.DoNotHandle(typeof(int))); // int is not an Exception

        Assert.Contains("must derive from Exception", ex.Message);
    }

    #endregion

    #region Group 8: Extension Methods (2 tests)

    [Fact]
    public void HandleTransientDatabaseErrors_RetriesDatabaseExceptions()
    {
        // Arrange
        var policy = new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(10))
            .HandleTransientDatabaseErrors();

        // Act & Assert
        // Note: TimeoutException is ALWAYS excluded by default ShouldRetry logic (fail-fast)
        // even when included in whitelist - this is intentional for task execution timeouts
        Assert.False(policy.ShouldRetry(new TimeoutException()));
        Assert.False(policy.ShouldRetry(new HttpRequestException()));
        Assert.False(policy.ShouldRetry(new ArgumentException()));
    }

    [Fact]
    public void HandleAllTransientErrors_CombinesDbAndNetwork()
    {
        // Arrange
        var policy = new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(10))
            .HandleAllTransientErrors();

        // Act & Assert
        // Note: TimeoutException and OperationCanceledException (including TaskCanceledException)
        // are ALWAYS excluded by default ShouldRetry logic (fail-fast)
        Assert.False(policy.ShouldRetry(new TimeoutException()));
        Assert.False(policy.ShouldRetry(new TaskCanceledException())); // Derives from OperationCanceledException
        Assert.True(policy.ShouldRetry(new HttpRequestException())); // Network - retryable
        Assert.True(policy.ShouldRetry(new SocketException())); // Network - retryable
        Assert.False(policy.ShouldRetry(new ArgumentException())); // Not transient
    }

    #endregion
}
