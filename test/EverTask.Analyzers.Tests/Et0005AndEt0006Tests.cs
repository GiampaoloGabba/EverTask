using System.Threading.Tasks;
using Xunit;
using AnalyzerVerifier = EverTask.Analyzers.Tests.CSharpAnalyzerVerifier<EverTask.Analyzers.PayloadContractAnalyzer>;

namespace EverTask.Analyzers.Tests;

public class Et0005JsonElementTests
{
    [Fact]
    public Task Object_property_is_flagged() => AnalyzerVerifier.VerifyAsync("""
        using EverTask.Abstractions;
        public class MyTask : IEverTask
        {
            public object {|ET0005:Data|} { get; set; }
        }
        """);

    [Fact]
    public Task Dictionary_string_object_is_flagged() => AnalyzerVerifier.VerifyAsync("""
        using System.Collections.Generic;
        using EverTask.Abstractions;
        public class MyTask : IEverTask
        {
            public Dictionary<string, object> {|ET0005:Bag|} { get; set; }
        }
        """);

    [Fact]
    public Task Typed_property_is_not_flagged() => AnalyzerVerifier.VerifyAsync("""
        using System.Collections.Generic;
        using EverTask.Abstractions;
        public class MyTask : IEverTask
        {
            public Dictionary<string, int> Counts { get; set; }
        }
        """);
}

public class Et0006NonSerializableTests
{
    [Fact]
    public void Rule_is_opt_in_disabled_by_default() =>
        Assert.False(DiagnosticDescriptors.NonSerializableType.IsEnabledByDefault);

    // The analyzer test harness force-enables every supported diagnostic, so the functional test below
    // exercises ET0006 directly; the opt-in (default-off) contract is pinned by the unit test above.
    [Fact]
    public Task IntPtr_property_is_flagged_when_enabled() => AnalyzerVerifier.VerifyAsync("""
        using EverTask.Abstractions;
        public class MyTask : IEverTask
        {
            public System.IntPtr {|ET0006:Handle|} { get; set; }
        }
        """);

    [Fact]
    public Task Delegate_property_is_flagged_when_enabled() => AnalyzerVerifier.VerifyAsync("""
        using EverTask.Abstractions;
        public class MyTask : IEverTask
        {
            public System.Action {|ET0006:Callback|} { get; set; }
        }
        """);
}
