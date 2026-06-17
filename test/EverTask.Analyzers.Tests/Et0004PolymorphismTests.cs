using System.Threading.Tasks;
using Xunit;
using AnalyzerVerifier = EverTask.Analyzers.Tests.CSharpAnalyzerVerifier<EverTask.Analyzers.PayloadContractAnalyzer>;
using CodeFixVerifier = EverTask.Analyzers.Tests.CSharpCodeFixVerifier<
    EverTask.Analyzers.PayloadContractAnalyzer, EverTask.Analyzers.Et0004ScaffoldPolymorphismCodeFix>;

namespace EverTask.Analyzers.Tests;

public class Et0004PolymorphismTests
{
    [Fact]
    public Task Abstract_property_without_polymorphism_is_flagged() => AnalyzerVerifier.VerifyAsync("""
        using EverTask.Abstractions;
        public abstract class Animal { }
        public class MyTask : IEverTask
        {
            public Animal {|ET0004:Pet|} { get; set; }
        }
        """);

    [Fact]
    public Task Interface_property_is_flagged() => AnalyzerVerifier.VerifyAsync("""
        using EverTask.Abstractions;
        public interface IAnimal { }
        public class MyTask : IEverTask
        {
            public IAnimal {|ET0004:Pet|} { get; set; }
        }
        """);

    [Fact]
    public Task Declared_polymorphism_is_not_flagged() => AnalyzerVerifier.VerifyAsync("""
        using System.Text.Json.Serialization;
        using EverTask.Abstractions;
        [JsonPolymorphic]
        [JsonDerivedType(typeof(Dog), "dog")]
        public abstract class Animal { }
        public class Dog : Animal { }
        public class MyTask : IEverTask
        {
            public Animal Pet { get; set; }
        }
        """);

    [Fact]
    public Task Concrete_property_is_not_flagged() => AnalyzerVerifier.VerifyAsync("""
        using EverTask.Abstractions;
        public class Animal { }
        public class MyTask : IEverTask
        {
            public Animal Pet { get; set; }
        }
        """);

    [Fact]
    public Task Code_fix_scaffolds_polymorphism_on_base() => CodeFixVerifier.VerifyAsync("""
        using EverTask.Abstractions;
        public abstract class Animal { }
        public class Dog : Animal { }
        public class MyTask : IEverTask
        {
            public Animal {|ET0004:Pet|} { get; set; }
        }
        """, """
        using EverTask.Abstractions;

        [System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
        [System.Text.Json.Serialization.JsonDerivedType(typeof(global::Dog), "dog")]
        public abstract class Animal { }
        public class Dog : Animal { }
        public class MyTask : IEverTask
        {
            public Animal Pet { get; set; }
        }
        """);
}
