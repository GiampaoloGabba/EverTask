using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace EverTask.Analyzers;

/// <summary>
/// Warns (ET0010) when <c>InTimeZone</c> sits on a fluent schedule chain that is provably a plain cadence,
/// which <c>RecurringTask.Validate</c> refuses at build time. It mirrors the runtime classification
/// (<c>ScheduleSemantics</c>): a Day, Week or Month interval, a calendar selector, a cron expression or an
/// occurrence provider makes the schedule calendar-anchored, and everything else is a constant step in
/// elapsed time.
/// </summary>
/// <remarks>
/// The runtime exception stays the contract; this only moves the report to the IDE. So the walk proves rather
/// than guesses: it follows the receiver chain back to the call that started it and gives up on anything it
/// does not recognize — a chain broken over a variable or a method, a call from outside the builder
/// interfaces, a second chain configuring the same builder in the same scope.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ScheduleTimeZoneAnalyzer : DiagnosticAnalyzer
{
    private const string InTimeZone = "InTimeZone";

    private static readonly ImmutableHashSet<string> Exclusions = ImmutableHashSet.Create(
        "Except", "ExceptWeekends");

    // Where a fresh interval selection begins. `Then` qualifies too: it is reachable only from the first-run
    // calls, none of which touch the grid.
    private static readonly ImmutableHashSet<string> ChainOrigins = ImmutableHashSet.Create("Schedule", "Then");

    private static readonly ImmutableArray<string> BuilderTypeNames = ImmutableArray.Create(
        "EverTask.Abstractions.IRecurringTaskBuilder",
        "EverTask.Abstractions.IIntervalSchedulerBuilder",
        "EverTask.Abstractions.IEverySchedulerBuilder",
        "EverTask.Abstractions.IHourSchedulerBuilder",
        "EverTask.Abstractions.IMinuteSchedulerBuilder",
        "EverTask.Abstractions.IDailyTimeSchedulerBuilder",
        "EverTask.Abstractions.IWeeklySchedulerBuilder",
        "EverTask.Abstractions.IMonthlySchedulerBuilder",
        "EverTask.Abstractions.IThenableSchedulerBuilder",
        "EverTask.Abstractions.IBuildableSchedulerBuilder");

    // A Day, Week or Month interval always snaps to a time of day, so it is calendar-anchored on its own.
    // `OnHours()` is absent on purpose: it populates no hour selector, so it classifies as Elapsed.
    private static readonly ImmutableHashSet<string> CalendarAnchoring = ImmutableHashSet.Create(
        "UseCron", "UseOccurrenceProvider",
        "EveryDay", "EveryWeek", "EveryMonth", "Days", "Weeks", "Months",
        "OnDay", "OnDays", "OnMonths", "OnFirst", "AtTime", "AtTimes");

    private static readonly ImmutableHashSet<string> ElapsedAnchoring = ImmutableHashSet.Create(
        "EverySecond", "EveryMinute", "EveryHour", "Seconds", "Minutes", "Hours",
        "AtMinute", "AtSecond");

    // Neither classifies: they refine the bounds or the delivery, and leave the grid alone.
    private static readonly ImmutableHashSet<string> Neutral = ImmutableHashSet.Create(
        "Every", InTimeZone, "OnMisfire", "WithDurableOccurrences", "BackfillFrom", "RunUntil");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.TimeZoneOnElapsedSchedule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var builders = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var name in BuilderTypeNames)
        {
            if (context.Compilation.GetTypeByMetadataName(name) is { } type)
                builders.Add(type);
        }

        if (builders.Count == 0)
            return;

        var known = builders.ToImmutable();
        context.RegisterOperationAction(c => AnalyzeInvocation(c, known), OperationKind.Invocation);
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, ImmutableHashSet<INamedTypeSymbol> builders)
    {
        var invocation = (IInvocationOperation)context.Operation;

        if (invocation.TargetMethod.Name != InTimeZone ||
            !IsBuilderMethod(invocation.TargetMethod, builders) ||
            HasExclusionInCompletedChain(invocation, builders) ||
            !IsProvablyElapsed(invocation.Instance, builders) ||
            !IsTheOnlyChainInScope(context, invocation, builders))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.TimeZoneOnElapsedSchedule, GetLocation(invocation)));
    }

    /// <summary>
    /// True when every call from <paramref name="receiver"/> back to the chain's origin is known, at least one
    /// of them anchors the grid in elapsed time, and none anchors it to a calendar. Anything else — an unknown
    /// call, a receiver that is not a builder invocation — is unprovable and reports nothing.
    /// </summary>
    private static bool IsProvablyElapsed(IOperation? receiver, ImmutableHashSet<INamedTypeSymbol> builders)
    {
        var sawElapsed = false;

        while (Unwrap(receiver) is IInvocationOperation step && IsBuilderMethod(step.TargetMethod, builders))
        {
            var name = step.TargetMethod.Name;

            if (ChainOrigins.Contains(name))
                return sawElapsed;

            if (CalendarAnchoring.Contains(name))
                return false;

            if (ElapsedAnchoring.Contains(name))
                sawElapsed = true;
            else if (!Neutral.Contains(name))
                return false;

            receiver = step.Instance;
        }

        return false;
    }

    /// <summary>
    /// True when a calendar exclusion occurs before or after <c>InTimeZone</c> on the same fluent chain.
    /// </summary>
    private static bool HasExclusionInCompletedChain(
        IInvocationOperation invocation, ImmutableHashSet<INamedTypeSymbol> builders)
    {
        IOperation? step = invocation;

        while (Unwrap(step) is IInvocationOperation current &&
               IsBuilderMethod(current.TargetMethod, builders))
        {
            if (Exclusions.Contains(current.TargetMethod.Name))
                return true;

            step = current.Instance;
        }

        step = invocation;

        while (TryGetOuterInvocation(step, builders, out var outer))
        {
            if (Exclusions.Contains(outer.TargetMethod.Name))
                return true;

            step = outer;
        }

        return false;
    }

    private static bool TryGetOuterInvocation(
        IOperation operation, ImmutableHashSet<INamedTypeSymbol> builders,
        out IInvocationOperation invocation)
    {
        IOperation current = operation;

        while (current.Parent is IConversionOperation conversion)
            current = conversion;

        if (current.Parent is IInvocationOperation outer &&
            IsBuilderMethod(outer.TargetMethod, builders) &&
            ReferenceEquals(Unwrap(outer.Instance), current))
        {
            invocation = outer;
            return true;
        }

        invocation = null!;
        return false;
    }

    /// <summary>
    /// Refuses a scope that configures the same builder through more than one chain: the second chain's
    /// intervals land on the very same schedule, so its shape is not the shape this one spells out.
    /// </summary>
    private static bool IsTheOnlyChainInScope(
        OperationAnalysisContext context, IInvocationOperation invocation, ImmutableHashSet<INamedTypeSymbol> builders)
    {
        var scope = invocation.Syntax.Ancestors().FirstOrDefault(a =>
            a is AnonymousFunctionExpressionSyntax or BaseMethodDeclarationSyntax or
                 AccessorDeclarationSyntax or LocalFunctionStatementSyntax or CompilationUnitSyntax);

        if (scope is null || invocation.SemanticModel is not { } model)
            return false;

        var chains = 0;

        foreach (var candidate in scope.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (candidate.Expression is not MemberAccessExpressionSyntax access ||
                !ChainOrigins.Contains(access.Name.Identifier.ValueText))
            {
                continue;
            }

            if (model.GetSymbolInfo(candidate, context.CancellationToken).Symbol is IMethodSymbol method &&
                IsBuilderMethod(method, builders) && ++chains > 1)
            {
                return false;
            }
        }

        return chains == 1;
    }

    private static bool IsBuilderMethod(IMethodSymbol method, ImmutableHashSet<INamedTypeSymbol> builders) =>
        method.ContainingType is { } type && builders.Contains(type);

    private static IOperation? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation conversion)
            operation = conversion.Operand;

        return operation;
    }

    // The whole chain is the invocation's span, so the report is narrowed to `InTimeZone(...)` itself.
    private static Location GetLocation(IInvocationOperation invocation) =>
        invocation.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } syntax
            ? Location.Create(syntax.SyntaxTree, TextSpan.FromBounds(access.Name.SpanStart, syntax.Span.End))
            : invocation.Syntax.GetLocation();
}
