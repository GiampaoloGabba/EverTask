using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace EverTask.Analyzers;

/// <summary>
/// Warns (ET0009) when a compile-time-constant delay handed to EverTask exceeds the largest delay .NET
/// timers support (<c>uint.MaxValue - 1</c> ms, about 49.7 days). Covered sites: the built-in retry policy
/// constructors (Linear throws at runtime, Exponential clamps), <c>SetDefaultTimeout</c> and handler
/// <c>Timeout</c> overrides (the worker clamps), and the audit cleanup intervals (the service clamps).
/// Only expressions the analyzer can fold to a constant are checked: <c>TimeSpan.FromX(literal)</c>,
/// <c>new TimeSpan(literals...)</c> and <c>TimeSpan.MaxValue</c>; anything computed at runtime is ignored.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TimerDelayLimitAnalyzer : DiagnosticAnalyzer
{
    private const double MaxTimerMilliseconds = 4_294_967_294d; // uint.MaxValue - 1

    private const string LinearPolicyTypeName      = "EverTask.Abstractions.LinearRetryPolicy";
    private const string ExponentialPolicyTypeName = "EverTask.Abstractions.ExponentialRetryPolicy";
    private const string ServiceConfigTypeName     = "Microsoft.Extensions.DependencyInjection.EverTaskServiceConfiguration";
    private const string QueueConfigTypeName       = "EverTask.Configuration.QueueConfiguration";
    private const string HandlerOptionsTypeName    = "EverTask.Abstractions.IEverTaskHandlerOptions";
    private const string CleanupOptionsTypeName    = "EverTask.Storage.EfCore.AuditCleanupOptions";
    private const string CleanupExtensionsTypeName = "EverTask.AuditCleanupServiceCollectionExtensions";

    private const string ThrowsBehavior       = "LinearRetryPolicy throws ArgumentOutOfRangeException at construction";
    private const string PolicyClampBehavior  = "ExponentialRetryPolicy clamps its delays to that ceiling";
    private const string TimeoutClampBehavior = "the worker clamps the timeout to that ceiling";
    private const string CleanupClampBehavior = "the cleanup service clamps the interval to that ceiling";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.DelayExceedsTimerLimit);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    // netstandard2.0: no records here
    private sealed class KnownTypes(
        INamedTypeSymbol? linearPolicy,
        INamedTypeSymbol? exponentialPolicy,
        INamedTypeSymbol? serviceConfig,
        INamedTypeSymbol? queueConfig,
        INamedTypeSymbol? handlerOptions,
        INamedTypeSymbol? cleanupOptions,
        INamedTypeSymbol? cleanupExtensions)
    {
        public INamedTypeSymbol? LinearPolicy      { get; } = linearPolicy;
        public INamedTypeSymbol? ExponentialPolicy { get; } = exponentialPolicy;
        public INamedTypeSymbol? ServiceConfig     { get; } = serviceConfig;
        public INamedTypeSymbol? QueueConfig       { get; } = queueConfig;
        public INamedTypeSymbol? HandlerOptions    { get; } = handlerOptions;
        public INamedTypeSymbol? CleanupOptions    { get; } = cleanupOptions;
        public INamedTypeSymbol? CleanupExtensions { get; } = cleanupExtensions;
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var compilation = context.Compilation;
        var types = new KnownTypes(
            compilation.GetTypeByMetadataName(LinearPolicyTypeName),
            compilation.GetTypeByMetadataName(ExponentialPolicyTypeName),
            compilation.GetTypeByMetadataName(ServiceConfigTypeName),
            compilation.GetTypeByMetadataName(QueueConfigTypeName),
            compilation.GetTypeByMetadataName(HandlerOptionsTypeName),
            compilation.GetTypeByMetadataName(CleanupOptionsTypeName),
            compilation.GetTypeByMetadataName(CleanupExtensionsTypeName));

        if (types is { LinearPolicy: null, ExponentialPolicy: null, ServiceConfig: null, QueueConfig: null,
                       HandlerOptions: null, CleanupOptions: null, CleanupExtensions: null })
            return;

        context.RegisterOperationAction(c => AnalyzeObjectCreation(c, types), OperationKind.ObjectCreation);
        context.RegisterOperationAction(c => AnalyzeInvocation(c, types), OperationKind.Invocation);
        context.RegisterOperationAction(c => AnalyzeAssignment(c, types), OperationKind.SimpleAssignment);

        if (types.HandlerOptions is not null)
            context.RegisterSyntaxNodeAction(c => AnalyzeTimeoutProperty(c, types), SyntaxKind.PropertyDeclaration);
    }

    private static void AnalyzeObjectCreation(OperationAnalysisContext context, KnownTypes types)
    {
        var creation = (IObjectCreationOperation)context.Operation;
        var type     = creation.Type;

        var isLinear      = Matches(type, types.LinearPolicy);
        var isExponential = Matches(type, types.ExponentialPolicy);
        if (!isLinear && !isExponential)
            return;

        var behavior = isLinear ? ThrowsBehavior : PolicyClampBehavior;

        foreach (var argument in creation.Arguments)
        {
            // LinearRetryPolicy(TimeSpan[]): inspect each element of an inline array initializer
            if (argument.Value is IArrayCreationOperation { Initializer: { } initializer })
            {
                foreach (var element in initializer.ElementValues)
                    ReportIfOverLimit(context, element, behavior);
                continue;
            }

            ReportIfOverLimit(context, argument.Value, behavior);
        }
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, KnownTypes types)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method     = invocation.TargetMethod;

        switch (method.Name)
        {
            case "SetDefaultTimeout" when Matches(method.ContainingType, types.ServiceConfig) ||
                                          Matches(method.ContainingType, types.QueueConfig):
            {
                foreach (var argument in invocation.Arguments)
                    ReportIfOverLimit(context, argument.Value, TimeoutClampBehavior);
                break;
            }

            case "AddAuditCleanup" when Matches(method.ContainingType, types.CleanupExtensions):
            {
                var hoursArgument = invocation.Arguments
                    .FirstOrDefault(a => a.Parameter?.Name == "cleanupIntervalHours");

                if (hoursArgument?.Value.ConstantValue is { HasValue: true, Value: int hours } &&
                    hours * 3_600_000d > MaxTimerMilliseconds)
                {
                    Report(context, hoursArgument.Value.Syntax, CleanupClampBehavior);
                }

                break;
            }
        }
    }

    private static void AnalyzeAssignment(OperationAnalysisContext context, KnownTypes types)
    {
        var assignment = (ISimpleAssignmentOperation)context.Operation;

        if (assignment.Target is not IPropertyReferenceOperation propertyReference ||
            propertyReference.Property.Name is not ("CleanupInterval" or "InitialDelay") ||
            !Matches(propertyReference.Property.ContainingType, types.CleanupOptions))
            return;

        ReportIfOverLimit(context, assignment.Value, CleanupClampBehavior);
    }

    // Handler `Timeout` overrides (`public override TimeSpan? Timeout => ...`) are property declarations,
    // not operations on a call site, so they need a syntax action.
    private static void AnalyzeTimeoutProperty(SyntaxNodeAnalysisContext context, KnownTypes types)
    {
        var property = (PropertyDeclarationSyntax)context.Node;
        if (property.Identifier.ValueText != "Timeout")
            return;

        var symbol = context.SemanticModel.GetDeclaredSymbol(property, context.CancellationToken);
        if (symbol?.ContainingType is not { } containingType ||
            !containingType.AllInterfaces.Any(i => Matches(i, types.HandlerOptions)))
            return;

        var expression = property.ExpressionBody?.Expression
                         ?? property.AccessorList?.Accessors
                             .FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration))?.ExpressionBody?.Expression
                         ?? property.Initializer?.Value;
        if (expression is null)
            return;

        if (context.SemanticModel.GetOperation(expression, context.CancellationToken) is { } operation &&
            TryGetConstantMilliseconds(operation) is > MaxTimerMilliseconds)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.DelayExceedsTimerLimit,
                expression.GetLocation(),
                expression.ToString(),
                TimeoutClampBehavior));
        }
    }

    private static void ReportIfOverLimit(OperationAnalysisContext context, IOperation operation, string behavior)
    {
        if (TryGetConstantMilliseconds(operation) is > MaxTimerMilliseconds)
            Report(context, operation.Syntax, behavior);
    }

    private static void Report(OperationAnalysisContext context, SyntaxNode syntax, string behavior)
    {
        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.DelayExceedsTimerLimit,
            syntax.GetLocation(),
            syntax.ToString(),
            behavior));
    }

    private static bool Matches(ISymbol? symbol, INamedTypeSymbol? expected) =>
        expected is not null && SymbolEqualityComparer.Default.Equals(symbol, expected);

    /// <summary>
    /// Folds the operation to milliseconds when it is a compile-time-constant TimeSpan expression:
    /// <c>TimeSpan.FromDays/Hours/Minutes/Seconds/Milliseconds(constant)</c>, <c>new TimeSpan(constants...)</c>
    /// or <c>TimeSpan.MaxValue</c>. Returns null for anything it cannot fold (runtime values stay unchecked).
    /// </summary>
    private static double? TryGetConstantMilliseconds(IOperation operation)
    {
        while (operation is IConversionOperation conversion)
            operation = conversion.Operand;

        switch (operation)
        {
            case IInvocationOperation { TargetMethod: { IsStatic: true } method, Arguments.Length: 1 } invocation
                when IsTimeSpanType(method.ContainingType) &&
                     invocation.Arguments[0].Value.ConstantValue is { HasValue: true, Value: { } raw } &&
                     ToDouble(raw) is { } value:
                return method.Name switch
                {
                    "FromDays"         => value * 86_400_000d,
                    "FromHours"        => value * 3_600_000d,
                    "FromMinutes"      => value * 60_000d,
                    "FromSeconds"      => value * 1_000d,
                    "FromMilliseconds" => value,
                    "FromTicks"        => value / TimeSpan.TicksPerMillisecond,
                    _                  => null
                };

            case IObjectCreationOperation newTimeSpan when IsTimeSpanType(newTimeSpan.Type):
            {
                var arguments = newTimeSpan.Arguments;
                var values    = new double[arguments.Length];
                for (var i = 0; i < arguments.Length; i++)
                {
                    if (arguments[i].Value.ConstantValue is not { HasValue: true, Value: { } raw } ||
                        ToDouble(raw) is not { } value)
                        return null;
                    values[i] = value;
                }

                return arguments.Length switch
                {
                    1 => values[0] / TimeSpan.TicksPerMillisecond,                                    // (ticks)
                    3 => values[0] * 3_600_000d + values[1] * 60_000d + values[2] * 1_000d,           // (h, m, s)
                    4 => values[0] * 86_400_000d + values[1] * 3_600_000d + values[2] * 60_000d +
                         values[3] * 1_000d,                                                          // (d, h, m, s)
                    5 => values[0] * 86_400_000d + values[1] * 3_600_000d + values[2] * 60_000d +
                         values[3] * 1_000d + values[4],                                              // (d, h, m, s, ms)
                    _ => null
                };
            }

            case IFieldReferenceOperation { Field: { IsStatic: true, Name: "MaxValue" } field }
                when IsTimeSpanType(field.ContainingType):
                return double.MaxValue;

            default:
                return null;
        }
    }

    private static bool IsTimeSpanType(ITypeSymbol? type) =>
        type is INamedTypeSymbol { SpecialType: SpecialType.None } named &&
        named.ToDisplayString() == "System.TimeSpan";

    private static double? ToDouble(object raw) => raw switch
    {
        int i    => i,
        long l   => l,
        double d => d,
        float f  => f,
        _        => null
    };
}
