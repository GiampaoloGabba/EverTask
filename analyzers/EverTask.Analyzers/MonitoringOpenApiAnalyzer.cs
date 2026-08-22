using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace EverTask.Analyzers;

/// <summary>
/// Warns (ET0008) when a net8.0 compilation opts into the monitoring OpenAPI document —
/// <c>EverTaskApiOptions.EnableOpenApiDocument = true</c> or <c>AddMonitoringApiScalar()</c> —
/// which is a no-op there: the built-in ASP.NET Core OpenAPI generator requires net9.0+.
/// On a multi-targeted host only the net8.0 compilation reports.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MonitoringOpenApiAnalyzer : DiagnosticAnalyzer
{
    private const string OptionsTypeName = "EverTask.Monitor.Api.Options.EverTaskApiOptions";
    private const string ScalarExtensionsTypeName = "EverTask.Monitor.Api.Scalar.Extensions.ServiceCollectionExtensions";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.MonitoringOpenApiOnNet8);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (!TargetsNet8(context.Compilation))
            return;

        var optionsType = context.Compilation.GetTypeByMetadataName(OptionsTypeName);
        if (optionsType is null)
            return;

        context.RegisterOperationAction(
            operationContext => AnalyzeAssignment(operationContext, optionsType),
            OperationKind.SimpleAssignment);
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
    }

    private static bool TargetsNet8(Compilation compilation)
    {
        // The SDK stamps every compilation with TargetFrameworkAttribute (e.g. ".NETCoreApp,Version=v8.0"),
        // so on a multi-targeted project each inner compilation is classified independently
        var targetFramework = compilation.Assembly.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() ==
                                 "System.Runtime.Versioning.TargetFrameworkAttribute")
            ?.ConstructorArguments.FirstOrDefault().Value as string;

        return targetFramework?.StartsWith(".NETCoreApp,Version=v8.", StringComparison.Ordinal) == true;
    }

    private static void AnalyzeAssignment(OperationAnalysisContext context, INamedTypeSymbol optionsType)
    {
        var assignment = (ISimpleAssignmentOperation)context.Operation;

        if (assignment.Target is not IPropertyReferenceOperation propertyReference ||
            propertyReference.Property.Name != "EnableOpenApiDocument" ||
            !SymbolEqualityComparer.Default.Equals(propertyReference.Property.ContainingType, optionsType))
            return;

        // Only a definite opt-in warns; assigning false (or a non-constant) is left alone
        if (assignment.Value.ConstantValue is not { HasValue: true, Value: true })
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.MonitoringOpenApiOnNet8,
            assignment.Syntax.GetLocation(),
            "EnableOpenApiDocument = true"));
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;

        if (invocation.TargetMethod.Name != "AddMonitoringApiScalar" ||
            invocation.TargetMethod.ContainingType?.ToDisplayString() != ScalarExtensionsTypeName)
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.MonitoringOpenApiOnNet8,
            invocation.Syntax.GetLocation(),
            "AddMonitoringApiScalar()"));
    }
}
