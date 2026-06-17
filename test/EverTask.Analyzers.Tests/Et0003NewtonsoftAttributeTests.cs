using System.Threading.Tasks;
using Xunit;
using AnalyzerVerifier = EverTask.Analyzers.Tests.CSharpAnalyzerVerifier<EverTask.Analyzers.PayloadContractAnalyzer>;
using CodeFixVerifier = EverTask.Analyzers.Tests.CSharpCodeFixVerifier<
    EverTask.Analyzers.PayloadContractAnalyzer, EverTask.Analyzers.Et0003NewtonsoftAttributeCodeFix>;

namespace EverTask.Analyzers.Tests;

public class Et0003NewtonsoftAttributeTests
{
    [Fact]
    public Task JsonProperty_is_flagged() => AnalyzerVerifier.VerifyAsync("""
        using Newtonsoft.Json;
        using EverTask.Abstractions;
        public class MyTask : IEverTask
        {
            [{|ET0003:JsonProperty("oid")|}]
            public System.Guid OrderId { get; set; }
        }
        """);

    [Fact]
    public Task Newtonsoft_JsonIgnore_is_flagged() => AnalyzerVerifier.VerifyAsync("""
        using Newtonsoft.Json;
        using EverTask.Abstractions;
        public class MyTask : IEverTask
        {
            [{|ET0003:JsonIgnore|}]
            public string Secret { get; set; } = "";
        }
        """);

    [Fact]
    public Task Stj_attributes_are_not_flagged() => AnalyzerVerifier.VerifyAsync("""
        using System.Text.Json.Serialization;
        using EverTask.Abstractions;
        public class MyTask : IEverTask
        {
            [JsonPropertyName("oid")]
            public System.Guid OrderId { get; set; }
        }
        """);

    [Fact]
    public Task Code_fix_removes_the_attribute() => CodeFixVerifier.VerifyAsync("""
        using Newtonsoft.Json;
        using EverTask.Abstractions;
        public class MyTask : IEverTask
        {
            [{|ET0003:JsonProperty("oid")|}]
            public System.Guid OrderId { get; set; }
        }
        """, """
        using Newtonsoft.Json;
        using EverTask.Abstractions;
        public class MyTask : IEverTask
        {
            public System.Guid OrderId { get; set; }
        }
        """, codeActionIndex: 0);

    [Fact]
    public Task Code_fix_maps_JsonProperty_to_JsonPropertyName() => CodeFixVerifier.VerifyAsync("""
        using Newtonsoft.Json;
        using EverTask.Abstractions;
        public class MyTask : IEverTask
        {
            [{|ET0003:JsonProperty("oid")|}]
            public System.Guid OrderId { get; set; }
        }
        """, """
        using Newtonsoft.Json;
        using EverTask.Abstractions;
        public class MyTask : IEverTask
        {
            [System.Text.Json.Serialization.JsonPropertyName("oid")]
            public System.Guid OrderId { get; set; }
        }
        """, codeActionIndex: 1);
}
