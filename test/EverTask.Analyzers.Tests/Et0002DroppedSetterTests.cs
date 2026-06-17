using System.Threading.Tasks;
using Xunit;
using AnalyzerVerifier = EverTask.Analyzers.Tests.CSharpAnalyzerVerifier<EverTask.Analyzers.PayloadContractAnalyzer>;
using CodeFixVerifier = EverTask.Analyzers.Tests.CSharpCodeFixVerifier<
    EverTask.Analyzers.PayloadContractAnalyzer, EverTask.Analyzers.Et0002MakeSetterPublicCodeFix>;

namespace EverTask.Analyzers.Tests;

public class Et0002DroppedSetterTests
{
    [Fact]
    public Task Private_setter_without_ctor_param_is_flagged() => AnalyzerVerifier.VerifyAsync("""
        using EverTask.Abstractions;
        public class MyTask : IEverTask
        {
            public int {|ET0002:Value|} { get; private set; }
        }
        """);

    [Fact]
    public Task Get_only_without_ctor_param_is_flagged() => AnalyzerVerifier.VerifyAsync("""
        using EverTask.Abstractions;
        public class MyTask : IEverTask
        {
            public int {|ET0002:Value|} { get; }
        }
        """);

    [Fact]
    public Task Init_setter_is_not_flagged() => AnalyzerVerifier.VerifyAsync("""
        using EverTask.Abstractions;
        public class MyTask : IEverTask
        {
            public int Value { get; init; }
        }
        """);

    [Fact]
    public Task Record_positional_parameter_is_not_flagged() => AnalyzerVerifier.VerifyAsync("""
        using EverTask.Abstractions;
        public record MyTask(int Value) : IEverTask;
        """);

    [Fact]
    public Task Matching_ctor_parameter_is_not_flagged() => AnalyzerVerifier.VerifyAsync("""
        using EverTask.Abstractions;
        public class MyTask : IEverTask
        {
            public MyTask(int value) => Value = value;
            public int Value { get; }
        }
        """);

    [Fact]
    public Task Code_fix_makes_setter_public() => CodeFixVerifier.VerifyAsync("""
        using EverTask.Abstractions;
        public class MyTask : IEverTask
        {
            public int {|ET0002:Value|} { get; private set; }
        }
        """, """
        using EverTask.Abstractions;
        public class MyTask : IEverTask
        {
            public int Value { get; set; }
        }
        """);
}
