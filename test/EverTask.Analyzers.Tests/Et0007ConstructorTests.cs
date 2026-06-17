using System.Threading.Tasks;
using Xunit;
using AnalyzerVerifier = EverTask.Analyzers.Tests.CSharpAnalyzerVerifier<EverTask.Analyzers.PayloadContractAnalyzer>;

namespace EverTask.Analyzers.Tests;

public class Et0007ConstructorTests
{
    [Fact]
    public Task Multiple_public_ctors_without_resolution_is_flagged() => AnalyzerVerifier.VerifyAsync("""
        using EverTask.Abstractions;
        public class {|ET0007:MyTask|} : IEverTask
        {
            public MyTask(int a) { }
            public MyTask(string b) { }
        }
        """);

    [Fact]
    public Task Parameterless_ctor_resolves_and_is_not_flagged() => AnalyzerVerifier.VerifyAsync("""
        using EverTask.Abstractions;
        public class MyTask : IEverTask
        {
            public MyTask() { }
            public MyTask(int a) { }
        }
        """);

    [Fact]
    public Task JsonConstructor_resolves_and_is_not_flagged() => AnalyzerVerifier.VerifyAsync("""
        using System.Text.Json.Serialization;
        using EverTask.Abstractions;
        public class MyTask : IEverTask
        {
            [JsonConstructor]
            public MyTask(int a) { }
            public MyTask(string b) { }
        }
        """);

    [Fact]
    public Task Single_ctor_is_not_flagged() => AnalyzerVerifier.VerifyAsync("""
        using EverTask.Abstractions;
        public class MyTask : IEverTask
        {
            public MyTask(int a) { }
        }
        """);

    [Fact]
    public Task Record_is_not_flagged() => AnalyzerVerifier.VerifyAsync("""
        using EverTask.Abstractions;
        public record MyTask(int A, string B) : IEverTask;
        """);

    [Fact]
    public Task ValueTuple_property_is_flagged_by_et0006() => AnalyzerVerifier.VerifyAsync("""
        using EverTask.Abstractions;
        public class MyTask : IEverTask
        {
            public (int, string) {|ET0006:Pair|} { get; set; }
        }
        """);
}
