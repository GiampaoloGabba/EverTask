using System.Threading.Tasks;
using Xunit;
using AnalyzerVerifier = EverTask.Analyzers.Tests.CSharpAnalyzerVerifier<EverTask.Analyzers.PayloadContractAnalyzer>;
using CodeFixVerifier = EverTask.Analyzers.Tests.CSharpCodeFixVerifier<
    EverTask.Analyzers.PayloadContractAnalyzer, EverTask.Analyzers.Et0001FieldToPropertyCodeFix>;

namespace EverTask.Analyzers.Tests;

public class Et0001PublicFieldTests
{
    [Fact]
    public Task Public_field_on_payload_is_flagged() => AnalyzerVerifier.VerifyAsync("""
        using EverTask.Abstractions;
        public class MyTask : IEverTask
        {
            public int {|ET0001:Counter|};
        }
        """);

    [Fact]
    public Task Property_is_not_flagged() => AnalyzerVerifier.VerifyAsync("""
        using EverTask.Abstractions;
        public class MyTask : IEverTask
        {
            public int Counter { get; set; }
        }
        """);

    [Fact]
    public Task Field_with_JsonInclude_is_not_flagged() => AnalyzerVerifier.VerifyAsync("""
        using System.Text.Json.Serialization;
        using EverTask.Abstractions;
        public class MyTask : IEverTask
        {
            [JsonInclude]
            public int Counter;
        }
        """);

    [Fact]
    public Task Code_fix_converts_field_to_property() => CodeFixVerifier.VerifyAsync("""
        using EverTask.Abstractions;
        public class MyTask : IEverTask
        {
            public int {|ET0001:Counter|};
        }
        """, """
        using EverTask.Abstractions;
        public class MyTask : IEverTask
        {
            public int Counter { get; set; }
        }
        """);
}
