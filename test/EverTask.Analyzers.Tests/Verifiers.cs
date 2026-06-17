using System.Collections.Immutable;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Text;

namespace EverTask.Analyzers.Tests;

/// <summary>
/// Shared test plumbing. The in-memory compilation gets a minimal <c>EverTask.Abstractions.IEverTask</c>
/// stub (the analyzer resolves the contract by metadata name, so a source-declared marker is enough) plus a
/// Newtonsoft.Json reference for the ET0003 cases. Use markup (<c>{|ET0001:member|}</c>) in the source to
/// declare expected diagnostics by id + span.
/// </summary>
internal static class TestEnvironment
{
    public const string IEverTaskStub =
        "namespace EverTask.Abstractions { public interface IEverTask { } }";

    public static readonly ReferenceAssemblies References =
        ReferenceAssemblies.Net.Net80.AddPackages(
            ImmutableArray.Create(new PackageIdentity("Newtonsoft.Json", "13.0.4")));
}

internal static class CSharpAnalyzerVerifier<TAnalyzer>
    where TAnalyzer : DiagnosticAnalyzer, new()
{
    public static Task VerifyAsync(string source, string? editorConfig = null)
    {
        var test = new Test { TestCode = source };
        test.TestState.Sources.Add(("IEverTask.cs", TestEnvironment.IEverTaskStub));
        if (editorConfig is not null)
            test.TestState.AnalyzerConfigFiles.Add(("/.editorconfig", editorConfig));
        return test.RunAsync();
    }

    private sealed class Test : CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
    {
        public Test() => ReferenceAssemblies = TestEnvironment.References;
    }
}

internal static class CSharpCodeFixVerifier<TAnalyzer, TCodeFix>
    where TAnalyzer : DiagnosticAnalyzer, new()
    where TCodeFix : CodeFixProvider, new()
{
    // Pin LF so the formatter's inserted lines match the LF test sources (avoids CRLF/LF diff noise).
    private const string LfConfig = "root = true\n[*.cs]\nend_of_line = lf\n";

    public static Task VerifyAsync(string source, string fixedSource, int? codeActionIndex = null)
    {
        var test = new Test { TestCode = source, FixedCode = fixedSource };
        test.TestState.Sources.Add(("IEverTask.cs", TestEnvironment.IEverTaskStub));
        test.TestState.AnalyzerConfigFiles.Add(("/.editorconfig", LfConfig));
        test.FixedState.Sources.Add(("IEverTask.cs", TestEnvironment.IEverTaskStub));
        test.FixedState.AnalyzerConfigFiles.Add(("/.editorconfig", LfConfig));
        if (codeActionIndex is { } index)
            test.CodeActionIndex = index;
        return test.RunAsync();
    }

    private sealed class Test : CSharpCodeFixTest<TAnalyzer, TCodeFix, DefaultVerifier>
    {
        public Test() => ReferenceAssemblies = TestEnvironment.References;
    }
}
