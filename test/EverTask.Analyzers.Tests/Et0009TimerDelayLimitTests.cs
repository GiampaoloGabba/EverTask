using Xunit;

namespace EverTask.Analyzers.Tests;

/// <summary>
/// ET0009: a compile-time-constant delay above the maximum timer duration (uint.MaxValue - 1 ms,
/// ~49.7 days) at an EverTask call site. The EverTask types are source stubs: the analyzer resolves
/// them by metadata name, so their shape only needs to match the real signatures.
/// </summary>
public class Et0009TimerDelayLimitTests
{
    private static Task VerifyAsync(string source) =>
        CSharpAnalyzerVerifier<Analyzers.TimerDelayLimitAnalyzer>.VerifyAsync(source, extraSource: Stubs);

    // Minimal mirrors of the real EverTask surface, matching full metadata names.
    private const string Stubs = """
        using System;
        namespace EverTask.Resilience
        {
            public class LinearRetryPolicy
            {
                public LinearRetryPolicy(int retryCount, TimeSpan retryDelay) { }
                public LinearRetryPolicy(TimeSpan[] retryDelays) { }
            }
            public class ExponentialRetryPolicy
            {
                public ExponentialRetryPolicy(int retryCount, TimeSpan initialDelay,
                    double backoffFactor = 2.0, TimeSpan? maxDelay = null, bool useJitter = false) { }
            }
        }
        namespace EverTask.Abstractions
        {
            public interface IEverTaskHandlerOptions { TimeSpan? Timeout { get; } }
            public abstract class EverTaskHandler<T> : IEverTaskHandlerOptions
            {
                public virtual TimeSpan? Timeout => null;
            }
        }
        namespace Microsoft.Extensions.DependencyInjection
        {
            public class EverTaskServiceConfiguration
            {
                public EverTaskServiceConfiguration SetDefaultTimeout(TimeSpan? timeout) => this;
            }
        }
        namespace EverTask.Configuration
        {
            public class QueueConfiguration
            {
                public QueueConfiguration SetDefaultTimeout(TimeSpan? timeout) => this;
            }
        }
        namespace EverTask.Storage.EfCore
        {
            public sealed class AuditCleanupOptions
            {
                public TimeSpan CleanupInterval { get; set; }
                public TimeSpan InitialDelay { get; set; }
            }
        }
        namespace EverTask
        {
            public static class AuditCleanupServiceCollectionExtensions
            {
                public static object AddAuditCleanup(this object services, object policy, int cleanupIntervalHours = 24)
                    => services;
            }
        }
        """;

    [Fact]
    public Task Reports_linear_policy_scalar_delay_over_the_limit() => VerifyAsync("""
        using System;
        using EverTask.Resilience;
        class C
        {
            object P = new LinearRetryPolicy(3, {|ET0009:TimeSpan.FromDays(60)|});
        }
        """);

    [Fact]
    public Task Reports_linear_policy_array_element_over_the_limit() => VerifyAsync("""
        using System;
        using EverTask.Resilience;
        class C
        {
            object P = new LinearRetryPolicy(new[] { TimeSpan.FromSeconds(1), {|ET0009:TimeSpan.FromDays(50)|} });
        }
        """);

    [Fact]
    public Task Reports_exponential_policy_max_delay_over_the_limit() => VerifyAsync("""
        using System;
        using EverTask.Resilience;
        class C
        {
            object P = new ExponentialRetryPolicy(3, TimeSpan.FromSeconds(1), maxDelay: {|ET0009:TimeSpan.FromDays(365)|});
        }
        """);

    [Fact]
    public Task Reports_handler_timeout_override_over_the_limit() => VerifyAsync("""
        using System;
        using EverTask.Abstractions;
        class MyHandler : EverTaskHandler<string>
        {
            public override TimeSpan? Timeout => {|ET0009:TimeSpan.FromDays(60)|};
        }
        """);

    [Fact]
    public Task Reports_set_default_timeout_over_the_limit() => VerifyAsync("""
        using System;
        using Microsoft.Extensions.DependencyInjection;
        class C
        {
            void M(EverTaskServiceConfiguration opt) => opt.SetDefaultTimeout({|ET0009:TimeSpan.FromDays(50)|});
        }
        """);

    [Fact]
    public Task Reports_timespan_maxvalue_and_ctor_forms() => VerifyAsync("""
        using System;
        using EverTask.Configuration;
        class C
        {
            void M(QueueConfiguration q)
            {
                q.SetDefaultTimeout({|ET0009:TimeSpan.MaxValue|});
                q.SetDefaultTimeout({|ET0009:new TimeSpan(50, 0, 0, 0)|});
            }
        }
        """);

    [Fact]
    public Task Reports_audit_cleanup_interval_over_the_limit() => VerifyAsync("""
        using System;
        using EverTask;
        using EverTask.Storage.EfCore;
        class C
        {
            void M(object services)
            {
                services.AddAuditCleanup(new object(), cleanupIntervalHours: {|ET0009:2160|});
                var options = new AuditCleanupOptions();
                options.CleanupInterval = {|ET0009:TimeSpan.FromDays(90)|};
            }
        }
        """);

    [Fact]
    public Task Does_not_report_valid_or_runtime_values() => VerifyAsync("""
        using System;
        using EverTask.Abstractions;
        using EverTask.Resilience;
        using Microsoft.Extensions.DependencyInjection;
        class MyHandler : EverTaskHandler<string>
        {
            public override TimeSpan? Timeout => TimeSpan.FromDays(49);
        }
        class C
        {
            object A = new LinearRetryPolicy(3, TimeSpan.FromSeconds(1));
            object B = new ExponentialRetryPolicy(30, TimeSpan.FromMilliseconds(500)); // clamps internally, no literal over-limit
            void M(EverTaskServiceConfiguration opt, TimeSpan runtimeValue)
            {
                opt.SetDefaultTimeout(TimeSpan.FromDays(49));
                opt.SetDefaultTimeout(runtimeValue); // not constant-foldable: ignored
                opt.SetDefaultTimeout(null);
            }
        }
        """);
}
